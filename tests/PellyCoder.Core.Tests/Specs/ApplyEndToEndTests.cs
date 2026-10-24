using System.IO.Abstractions;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Specs;

/// <summary><c>pelly apply</c> driven through the real FSM (scripted LLM, user and build) instead of a fake executor.</summary>
public sealed class ApplyEndToEndTests : IDisposable
{
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(60);

    private const string CalcSource =
        "namespace Demo;\n" +
        "\n" +
        "public class Calc\n" +
        "{\n" +
        "    public int Divide(int a, int b)\n" +
        "    {\n" +
        "        return a / b;\n" +
        "    }\n" +
        "\n" +
        "    public int Multiply(int a, int b)\n" +
        "    {\n" +
        "        return a * b;\n" +
        "    }\n" +
        "}\n";

    private const string DivideOld = "        return a / b;";
    private const string DivideNew = "        if (b == 0) throw new DivideByZeroException();\n        return a / b;";
    private const string MultiplyOld = "        return a * b;";
    private const string MultiplyBroken = "        return a * b";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new() { IsInteractive = true };
    private readonly PellyOptions _options = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero));

    public ApplyEndToEndTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _processes.WhenEmpty(new ProcessResult(0, "", "", [], TimeSpan.Zero, false));
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        Directory.CreateDirectory(Path.Combine(_root, "changes"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        File.WriteAllText(Path.Combine(_root, "Demo", "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(CalcPath, CalcSource);
        File.WriteAllText(SpecPath,
            "---\nid: calc\nstatus: draft\n---\n# 계산기\n\n## 수용 기준\n- AC1: 0 나눗셈 방어\n\n## 작업\n" +
            "- [ ] T1 cs Demo/Calc.cs#Calc.Divide Throw DivideByZeroException when b is zero.\n" +
            "- [ ] T2 cs Demo/Calc.cs#Calc.Multiply Keep Multiply returning the product.\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");

    private string SpecPath => Path.Combine(_root, "changes", "calc.md");

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private Task<int> Apply()
    {
        var guard = new FileGuard(_fs, TimeProvider.System);
        // A fresh orchestrator per task: one instance is not meant to be reused across runs.
        var executor = new FsmTaskExecutor((req, ct) =>
            FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes).RunDetailedAsync(req, ct));
        var runner = new SpecRunner(_fs, guard, executor, _ui, _time, 160);
        return runner.RunAsync(SpecPath, new ApplyOptions(false, false, false, true, false, null, false), TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);
    }

    [Fact]
    public async Task Two_tasks_on_one_file_apply_in_order_and_archive()
    {
        var onDisk = new List<string>();
        _backend.BeforeRespond = _ =>
        {
            onDisk.Add(File.ReadAllText(CalcPath));
            return Task.CompletedTask;
        };
        _backend.Enqueue(Block(DivideOld, DivideNew));
        _backend.Enqueue(Block(MultiplyOld, "        return checked(a * b);"));

        var code = await Apply();

        Assert.Equal(0, code);
        var final = File.ReadAllText(CalcPath);
        Assert.Contains("throw new DivideByZeroException()", final);
        Assert.Contains("return checked(a * b);", final);

        // The second task works on the file as the first one left it. The prompt only carries the target member and the
        // other members' signatures, so the file on disk at the time of each LLM call is what shows the order.
        Assert.Equal(2, _backend.Requests.Count);
        Assert.Equal(2, onDisk.Count);
        Assert.DoesNotContain("DivideByZeroException();", onDisk[0]);
        Assert.Contains("DivideByZeroException();", onDisk[1]);
        Assert.Contains("Calc.Divide", _backend.Requests[0].RawPrompt);
        Assert.Contains("Calc.Multiply", _backend.Requests[1].RawPrompt);

        Assert.False(File.Exists(SpecPath));
        var archived = File.ReadAllText(Path.Combine(_root, "changes", "archive", "2026-10-24-calc.md"));
        Assert.Contains("status: done", archived);
        var lines = archived.Split('\n').Where(l => l.StartsWith("- [x] T")).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Matches(@"<!-- run: \S+ -->$", l.TrimEnd()));
    }

    [Fact]
    public async Task Second_task_verify_failure_rolls_back_only_that_task_and_marks_failed()
    {
        _backend.Enqueue(Block(DivideOld, DivideNew));
        _backend.Enqueue(Block(MultiplyOld, MultiplyBroken));
        for (var i = 0; i < 3; i++)
        {
            _backend.Enqueue("Retry.</think>", evalCount: 5);
            _backend.Enqueue(Block(MultiplyBroken, MultiplyBroken));
        }

        var code = await Apply();

        Assert.Equal(1, code);
        // T1 stays applied, T2 is back to its original text.
        Assert.Equal(CalcSource.Replace(DivideOld, DivideNew), File.ReadAllText(CalcPath));

        Assert.True(File.Exists(SpecPath));
        var spec = File.ReadAllText(SpecPath);
        Assert.Matches(@"- \[x\] T1 cs Demo/Calc\.cs#Calc\.Divide .* <!-- run: \S+ -->", spec);
        Assert.Matches(@"- \[ \] T2 cs Demo/Calc\.cs#Calc\.Multiply .* <!-- failed: \S+ SyntaxErrors -->", spec);
        Assert.Contains("status: in-progress", spec);
        Assert.False(Directory.Exists(Path.Combine(_root, "changes", "archive")));
    }
}
