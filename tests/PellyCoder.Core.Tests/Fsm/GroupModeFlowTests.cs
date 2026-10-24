using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>
/// A run inside a task group: no baseline or build (the group builds once at its end), the target's signature and name may
/// change, retries follow the renamed member, and the group's feedback reaches the first prompt.
/// </summary>
public sealed class GroupModeFlowTests : IDisposable
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
        "}\n";

    private const string Header = "    public int Divide(int a, int b)";
    private const string Original = "        return a / b;";
    private const string Broken = "        return a / b";
    private const string Guarded = "        if (b == 0) throw new DivideByZeroException();\n        return a / b;";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-group-flow-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly PellyOptions _options = new();

    public GroupModeFlowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        File.WriteAllText(CsprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(CalcPath, CalcSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");
    private string CsprojPath => Path.Combine(_root, "Demo", "Demo.csproj");

    private static RunRequest Req(string path, GroupRun? group, int? maxRetries = null) =>
        new(path, null, "Divide", null, "Throw DivideByZeroException when b is zero.", true, false, maxRetries, Group: group);

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private Task<RunResult> Run(RunRequest request) =>
        FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes, null)
            .RunDetailedAsync(request, TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    private static ProcessResult Build() => new(0, "", "", Encoding.UTF8.GetBytes(""), TimeSpan.FromSeconds(1), false);

    /// <summary>The prompt's last <c>[EDITABLE: target]</c> section (the few-shot example has one too).</summary>
    private static string TargetSection(string prompt) => prompt[prompt.LastIndexOf("[EDITABLE: target]", StringComparison.Ordinal)..];

    [Fact]
    public async Task Group_run_starts_no_baseline_and_no_build()
    {
        _backend.Enqueue(Block(Original, Guarded));

        var result = await Run(Req(CalcPath, new GroupRun(null)));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(_processes.Calls);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task Group_run_accepts_signature_change()
    {
        _backend.Enqueue(Block(Header, "    public long Divide(int a, int b)"));

        var result = await Run(Req(CalcPath, new GroupRun(null)));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(_processes.Calls);
        Assert.Equal(CalcSource.Replace("public int Divide", "public long Divide"), File.ReadAllText(CalcPath));
        Assert.Equal("Calc.Divide(int, int)", result.TargetDisplay);
    }

    [Fact]
    public async Task Rename_then_verify_failure_retries_on_the_renamed_member()
    {
        _backend.Enqueue(Block(Header + "\n    {\n" + Original, "    public int Quotient(int a, int b)\n    {\n" + Broken));
        _backend.Enqueue("Add the semicolon.</think>", evalCount: 10);
        _backend.Enqueue(Block(Broken, Guarded));

        var result = await Run(Req(CalcPath, new GroupRun(null)));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(_processes.Calls);
        Assert.Equal(CalcSource.Replace("Divide", "Quotient").Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Equal("Calc.Quotient(int, int)", result.TargetDisplay);
        var target = TargetSection(_backend.Requests[1].RawPrompt);
        Assert.Contains("public int Quotient(int a, int b)", target);
        Assert.DoesNotContain("Divide(", target);
    }

    [Fact]
    public async Task Initial_feedback_reaches_first_prompt()
    {
        const string text = "BUILD FAILED (1 new errors)\nDemo/Use.cs(3): CS1501: No overload for method 'Divide' takes 2 arguments";
        _backend.Enqueue(Block(Original, Guarded));

        var result = await Run(Req(CalcPath, new GroupRun(new Feedback(text, 20))));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(text, _backend.Requests[0].RawPrompt);
    }

    [Fact]
    public async Task Non_group_run_is_unchanged()
    {
        _processes.Enqueue(Build()); // baseline
        _backend.Enqueue(Block(Header, "    public long Divide(int a, int b)"));
        _backend.Enqueue("Keep the signature.</think>", evalCount: 10);
        _backend.Enqueue(Block(Header, "    public long Divide(int a, int b)"));

        var result = await Run(Req(CalcPath, null, maxRetries: 1));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("ShapeRejected", result.FailureKind);
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
        Assert.Single(_processes.Calls);
        Assert.Contains("SHAPE REJECTED: signature of Calc.Divide(int, int) changed", _backend.Requests[1].RawPrompt);
    }
}
