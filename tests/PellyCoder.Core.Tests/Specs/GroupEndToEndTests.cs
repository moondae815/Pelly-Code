using System.IO.Abstractions;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Specs;

/// <summary>A task group driven through the real FSM and the real <see cref="GroupVerifier"/> (scripted LLM, user and build).</summary>
public sealed class GroupEndToEndTests : IDisposable
{
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(60);

    private const string InterfaceOld = "    Task CancelAsync(int id);";
    private const string InterfaceNew = "    Task<bool> CancelAsync(int id);";
    private const string ServiceOld = "    public async Task CancelAsync(int id)\n    {\n        await Task.Delay(1);\n    }";
    private const string ServiceNew = "    public async Task<bool> CancelAsync(int id)\n    {\n        await Task.Delay(1);\n        return true;\n    }";
    private const string ControllerOld = "        await _svc.CancelAsync(id);";
    private const string ControllerNew = "        var ok = await _svc.CancelAsync(id);\n        if (!ok) throw new InvalidOperationException();";

    private const string InterfaceSource =
        "namespace Lib;\n\npublic interface IOrderService\n{\n" + InterfaceOld + "\n}\n";
    private const string ServiceSource =
        "namespace Lib;\n\npublic class OrderService : IOrderService\n{\n" + ServiceOld + "\n}\n";
    private const string ControllerSource =
        "namespace App;\n\npublic class OrdersController(Lib.IOrderService svc)\n{\n    private readonly Lib.IOrderService _svc = svc;\n\n" +
        "    public async Task Cancel(int id)\n    {\n" + ControllerOld + "\n    }\n}\n";

    private const string T1 = "- [ ] T1 cs Lib/IOrderService.cs#IOrderService.CancelAsync 취소 결과를 bool로 돌려준다 [G1]\n";
    private const string T2 = "- [ ] T2 cs Lib/OrderService.cs#OrderService.CancelAsync 구현이 bool을 돌려준다 [G1]\n";
    private const string T3 = "- [ ] T3 cs App/OrdersController.cs#OrdersController.Cancel 취소 결과를 확인한다 [G1]\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-group-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new() { IsInteractive = true };
    private readonly PellyOptions _options = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero));
    private readonly List<string> _builds = [];
    private Func<string, int, string[]> _buildOutput = (_, _) => [];

    public GroupEndToEndTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _processes.WhenEmpty(new ProcessResult(0, "", "", [], TimeSpan.Zero, false));
        // Build calls answer per csproj; any other process (tool probes) succeeds.
        var buildsPerProject = new Dictionary<string, int>();
        Func<ProcessSpec, CancellationToken, Task<ProcessResult>> handler = (spec, _) =>
        {
            if (spec.Arguments.Count < 2 || spec.Arguments[0] != "build")
                return Task.FromResult(new ProcessResult(0, "", "", [], TimeSpan.Zero, false));
            var project = Path.GetFileNameWithoutExtension(spec.Arguments[1]);
            _builds.Add(project);
            buildsPerProject[project] = buildsPerProject.GetValueOrDefault(project) + 1;
            var lines = _buildOutput(project, buildsPerProject[project]);
            var stdout = string.Join("\n", lines);
            return Task.FromResult(new ProcessResult(lines.Length == 0 ? 0 : 1, stdout, "", Encoding.UTF8.GetBytes(stdout), TimeSpan.FromSeconds(1), false));
        };
        for (var i = 0; i < 40; i++) _processes.Enqueue(handler);

        Directory.CreateDirectory(Path.Combine(_root, "Lib"));
        Directory.CreateDirectory(Path.Combine(_root, "App"));
        Directory.CreateDirectory(Path.Combine(_root, "changes"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        File.WriteAllText(Path.Combine(_root, "Lib", "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(_root, "App", "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <ProjectReference Include=\"../Lib/Lib.csproj\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(InterfacePath, InterfaceSource);
        File.WriteAllText(ServicePath, ServiceSource);
        File.WriteAllText(ControllerPath, ControllerSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string InterfacePath => Path.Combine(_root, "Lib", "IOrderService.cs");
    private string ServicePath => Path.Combine(_root, "Lib", "OrderService.cs");
    private string ControllerPath => Path.Combine(_root, "App", "OrdersController.cs");
    private string SpecPath => Path.Combine(_root, "changes", "cancel.md");

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private void WriteSpec(string tasks) =>
        File.WriteAllText(SpecPath, "---\nid: cancel\nstatus: draft\n---\n# 취소\n\n## 수용 기준\n- AC1: 취소 결과를 돌려준다\n\n## 작업\n" + tasks);

    private Task<int> Apply()
    {
        var guard = new FileGuard(_fs, TimeProvider.System);
        var executor = new FsmTaskExecutor((req, ct) =>
            FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes).RunDetailedAsync(req, ct));
        var groups = new GroupVerifier(_fs, new BuildVerifier(_processes, _options.Build), new BuildToolResolver(_fs, _processes, _options.Build), () => false);
        var runner = new SpecRunner(_fs, guard, executor, _ui, _time, 160, null, groups,
            new FeedbackBuilder(QwenTokenCounter.Shared, _options.Budget.Feedback), _options.Retry.Max);
        return runner.RunAsync(SpecPath, new ApplyOptions(false, false, false, true, false, null, false), TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);
    }

    [Fact]
    public async Task Signature_change_group_applies_three_tasks_and_archives()
    {
        WriteSpec(T1 + T2 + T3);
        _backend.Enqueue(Block(InterfaceOld, InterfaceNew));
        _backend.Enqueue(Block(ServiceOld, ServiceNew));
        _backend.Enqueue(Block(ControllerOld, ControllerNew));

        var code = await Apply();

        Assert.Equal(0, code);
        Assert.Contains("Task<bool> CancelAsync", File.ReadAllText(InterfacePath));
        Assert.Contains("return true;", File.ReadAllText(ServicePath));
        Assert.Contains("var ok = await _svc.CancelAsync(id);", File.ReadAllText(ControllerPath));
        Assert.False(File.Exists(SpecPath));
        var archived = File.ReadAllText(Path.Combine(_root, "changes", "archive", "2026-10-25-cancel.md"));
        Assert.Contains("status: done", archived);
        Assert.Equal(3, archived.Split('\n').Count(l => l.StartsWith("- [x] T")));

        // Baseline and verification each build Lib and App (App because it references Lib).
        Assert.Equal(2, _builds.Count(b => b == "Lib"));
        Assert.Equal(2, _builds.Count(b => b == "App"));
        Assert.Equal(4, _builds.Count);
    }

    [Fact]
    public async Task Missing_caller_rolls_back_group()
    {
        WriteSpec(T1 + T2);
        _backend.Enqueue(Block(InterfaceOld, InterfaceNew));
        _backend.Enqueue(Block(ServiceOld, ServiceNew));
        // The verification build (second call per project) reports the caller that the spec did not list.
        _buildOutput = (project, n) => project == "App" && n == 2
            ? [$"{ControllerPath}(9,16): error CS0029: Cannot implicitly convert type 'bool' to 'void' [{Path.Combine(_root, "App", "App.csproj")}]"]
            : [];

        var code = await Apply();

        Assert.Equal(1, code);
        Assert.Equal(InterfaceSource, File.ReadAllText(InterfacePath));
        Assert.Equal(ServiceSource, File.ReadAllText(ServicePath));
        Assert.Equal(ControllerSource, File.ReadAllText(ControllerPath));
        Assert.Contains(_ui.Warnings, w => w.Contains("App/OrdersController.cs#Cancel"));
        var spec = File.ReadAllText(SpecPath);
        Assert.Matches(@"T1 .* \[G1\] <!-- failed: \S+ GroupMissingTarget -->", spec);
        Assert.Matches(@"T2 .* \[G1\] <!-- failed: \S+ GroupMissingTarget -->", spec);
        Assert.Contains("status: in-progress", spec);
        Assert.False(Directory.Exists(Path.Combine(_root, "changes", "archive")));
    }
}
