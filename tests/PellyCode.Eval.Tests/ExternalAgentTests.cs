using PellyCode.Core.Abstractions;
using PellyCode.Core.Fsm;
using PellyCode.Core.Models;
using PellyCode.Eval;

namespace PellyCode.Eval.Tests;

public sealed class ExternalAgentTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "pelly-extagent-" + Guid.NewGuid().ToString("N")[..8]);
    private string WorkDir => Path.Combine(_out, "work", "cs-x-001");

    public void Dispose()
    {
        try { Directory.Delete(_out, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public ProcessSpec? Spec;
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct) { Spec = spec; return Task.FromResult(result); }
    }

    private static ProcessResult Result(int exit = 0, bool timedOut = false, string stdout = "", string stderr = "") =>
        new(exit, stdout, stderr, [], TimeSpan.FromSeconds(2), timedOut);

    private RunRequest Request(EditKind kind = EditKind.CSharp, string? method = "CancelAsync", int? line = null, string? obj = null)
    {
        var file = Path.Combine(WorkDir, "Modern", "Shop", "OrderService.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return new RunRequest(file, null, method, line, "요청 문장", true, false, null, ObjectName: obj, Kind: kind);
    }

    [Fact]
    public async Task ExternalAgent_PassesArgsInOrder()
    {
        var runner = new FakeRunner(Result());
        var exit = await ExternalAgent.Create("/x/agent.sh", runner)(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        var spec = runner.Spec!;
        Assert.Equal(Path.GetFullPath(WorkDir), spec.WorkingDirectory);
        Assert.Equal(["/x/agent.sh", Path.GetFullPath(WorkDir), "Modern/Shop/OrderService.cs", "cs", "CancelAsync", "", "", "요청 문장"], spec.Arguments);
    }

    [Fact]
    public async Task ExternalAgent_PassesSqlKindLineAndObject()
    {
        var runner = new FakeRunner(Result());
        await ExternalAgent.Create("a.sh", runner)(Request(EditKind.Sql, method: null, line: 7, obj: "usp_Cancel"), TestContext.Current.CancellationToken);

        Assert.Equal(["sql", "", "7", "usp_Cancel"], runner.Spec!.Arguments.Skip(3).Take(4));
    }

    [Fact]
    public async Task ExternalAgent_TimeoutIs124()
    {
        var runner = new FakeRunner(Result(exit: -1, timedOut: true));
        var exit = await ExternalAgent.Create("a.sh", runner, TimeSpan.FromMilliseconds(50))(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(124, exit);
        Assert.Equal(TimeSpan.FromMilliseconds(50), runner.Spec!.Timeout);
    }

    [Fact]
    public async Task ExternalAgent_DefaultTimeoutIs15Minutes()
    {
        var runner = new FakeRunner(Result());
        await ExternalAgent.Create("a.sh", runner)(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromMinutes(15), runner.Spec!.Timeout);
    }

    [Fact]
    public async Task ExternalAgent_WritesAgentLog()
    {
        var runner = new FakeRunner(Result(exit: 3, stdout: "도구 호출 pelly_verify", stderr: "경고"));
        var exit = await ExternalAgent.Create("a.sh", runner)(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(3, exit);
        var log = await File.ReadAllTextAsync(Path.Combine(WorkDir, "agent.log"), TestContext.Current.CancellationToken);
        Assert.StartsWith("exit=3 timedOut=False", log);
        Assert.Contains("--- stdout\n도구 호출 pelly_verify", log);
        Assert.Contains("--- stderr\n경고", log);
    }
}
