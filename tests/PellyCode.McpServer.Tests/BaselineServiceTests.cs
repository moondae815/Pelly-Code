using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PellyCode.Core.Config;
using PellyCode.Core.Models;
using PellyCode.Core.Processes;
using PellyCode.Core.Specs;
using PellyCode.Core.Testing;
using PellyCode.Core.Workspaces;
using PellyCode.McpServer.Baseline;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Tests;

public sealed class BaselineServiceTests : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongWait = TimeSpan.FromMinutes(10);
    private readonly string _base = Path.Combine(Path.GetTempPath(), "pelly-baseline-" + Guid.NewGuid().ToString("N"));
    private readonly string _dir;
    private readonly string _mirror;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-17T09:00:00Z"));
    private readonly FakeVerifier _verifier = new();
    private readonly FakeTests _tests = new();
    private readonly List<BaselineService> _services = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BaselineServiceTests()
    {
        _dir = Path.Combine(_base, "repo");
        _mirror = Path.Combine(_base, "mcp-base", "0123456789abcdef");
        Directory.CreateDirectory(_dir);
        Git("init", "-q");
        Git("config", "user.name", "t");
        Git("config", "user.email", "t@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
    }

    public void Dispose()
    {
        foreach (var s in _services)
        {
            try { s.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            s.Dispose();
        }
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output.Trim();
    }

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(text));
    }

    private string Commit() { Git("add", "-A"); Git("commit", "-q", "-m", "c"); return Git("rev-parse", "HEAD"); }

    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    private const string TestCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\App\\App.csproj\" /></ItemGroup></Project>";

    /// <summary>App (and App.Tests that references it) listed in a root .slnx.</summary>
    private string SolutionRepo(bool withTests = true)
    {
        Write("App/App.csproj", Csproj);
        Write("App/A.cs", "class A {}");
        var sln = "<Solution><Project Path=\"App/App.csproj\" />";
        if (withTests)
        {
            Write("App.Tests/App.Tests.csproj", TestCsproj);
            Write("App.Tests/T.cs", "class T {}");
            sln += "<Project Path=\"App.Tests/App.Tests.csproj\" />";
        }
        Write("Shop.slnx", sln + "</Solution>");
        return Commit();
    }

    private string PlainRepo()
    {
        Write("App/App.csproj", Csproj);
        Write("App/A.cs", "class A {}");
        return Commit();
    }

    private BaselineService NewService()
    {
        var fs = new FileSystem();
        var git = new GitRepo(new SystemProcessRunner(TimeProvider.System), _dir);
        var s = new BaselineService(fs, _dir, _mirror, git, _verifier, _tests, new TestOptions(), _time, NullLogger<BaselineService>.Instance);
        _services.Add(s);
        return s;
    }

    private static async Task WaitUntil(Func<bool> done, Action? step = null)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("condition not reached");
            step?.Invoke();
            await Task.Delay(10, Ct);
        }
    }

    private static Baseline.Baseline Done(BaselineService s) => s.Last!;
    private static bool Settled(BaselineService s) => s.Last?.Status is BaselineStatus.Ready or BaselineStatus.Failed;

    [Fact]
    public async Task Start_PrebuildsSolution()
    {
        var head = SolutionRepo();
        var s = NewService();

        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));

        var b = Done(s);
        Assert.Equal(BaselineStatus.Ready, b.Status);
        Assert.Equal(head, b.HeadSha);
        Assert.Equal(1, _verifier.Calls);
        Assert.Equal(_mirror, _verifier.Roots[0]);
        Assert.Equal(
            new[] { Path.Combine(_mirror, "App", "App.csproj"), Path.Combine(_mirror, "App.Tests", "App.Tests.csproj") },
            _verifier.Requests[0]);
        Assert.Equal(["App.Tests/App.Tests.csproj"], b.TestProjectsRun);
        Assert.True(b.Tests.ContainsKey("App.Tests/App.Tests.csproj"));
        Assert.Equal(Path.Combine(_mirror, "App.Tests", "App.Tests.csproj"), _tests.Projects.Single());
        Assert.True(File.Exists(s.CachePath));
    }

    [Fact]
    public async Task HeadChange_Rebuilds()
    {
        SolutionRepo();
        var s = NewService();
        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));
        Assert.Equal(1, _verifier.Calls);

        Write("App/B.cs", "class B {}");
        var second = Commit();

        await WaitUntil(() => _verifier.Calls == 2 && Settled(s) && s.Last!.HeadSha == second, () => _time.Advance(Poll));
        Assert.Equal(BaselineStatus.Ready, s.Last!.Status);
    }

    [Fact]
    public async Task HeadChange_SeenOnToolCall()
    {
        PlainRepo();
        Write("Shop.slnx", "<Solution><Project Path=\"App/App.csproj\" /></Solution>");
        Commit();
        var s = NewService();
        var first = await s.GetAsync([], LongWait, null, Ct);
        Assert.Equal(BaselineStatus.Ready, first.Status);
        Assert.Equal(1, _verifier.Calls);

        Write("App/B.cs", "class B {}");
        var second = Commit();
        var b = await s.GetAsync([], LongWait, null, Ct);

        Assert.Equal(second, b.HeadSha);
        Assert.Equal(BaselineStatus.Ready, b.Status);
        Assert.Equal(2, _verifier.Calls);
    }

    [Fact]
    public async Task Baseline_ConcurrentRequests_OneBuild()
    {
        PlainRepo();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _verifier.Handler = async (_, _) => { await gate.Task; return new GroupBuild(true, null, null, []); };
        var s = NewService();

        var a = s.GetAsync(["App/App.csproj"], LongWait, null, Ct);
        var b = s.GetAsync(["App/App.csproj"], LongWait, null, Ct);
        await WaitUntil(() => _verifier.Calls >= 1);
        gate.SetResult();
        var results = await Task.WhenAll(a, b);

        Assert.Equal(1, _verifier.Calls);
        Assert.All(results, r => Assert.Equal(BaselineStatus.Ready, r.Status));
    }

    [Fact]
    public async Task HeadCompileErrors_AreBaseline_NotFailure()
    {
        SolutionRepo(withTests: false);
        var error = new Diagnostic(Path.Combine(_mirror, "App", "A.cs"), 1, 1, "error", "CS1002", "; expected");
        _verifier.Handler = (_, _) => Task.FromResult(new GroupBuild(true, null, null, [error]));
        var s = NewService();

        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));

        var b = Done(s);
        Assert.Equal(BaselineStatus.Ready, b.Status);
        Assert.Null(b.FailureReason);
        Assert.Equal([error], b.Build!.Errors);
    }

    private static GroupBuild RestoreFailed() =>
        new(false, "그룹 빌드 검증 불가 (App/App.csproj): NU1101 복원 실패", GroupVerifier.UnverifiableKind, []);

    /// <summary>App and Lib in the root solution, no test projects.</summary>
    private void TwoProjectRepo()
    {
        PlainRepo();
        Write("Lib/Lib.csproj", Csproj);
        Write("Shop.slnx", "<Solution><Project Path=\"App/App.csproj\" /><Project Path=\"Lib/Lib.csproj\" /></Solution>");
        Commit();
    }

    private static bool Has(IReadOnlyList<string> projects, string name) => projects.Any(p => p.Contains(name, StringComparison.Ordinal));

    [Fact]
    public async Task Failure_OfOneProject_DoesNotPoisonOthers()
    {
        PlainRepo();
        Write("Other/Other.csproj", Csproj);
        Write("Shop.slnx", "<Solution><Project Path=\"App/App.csproj\" /></Solution>");
        Commit();
        var e1 = new Diagnostic(Path.Combine(_mirror, "App", "A.cs"), 1, 1, "error", "CS1", "a");
        _verifier.Handler = (p, _) => Task.FromResult(Has(p, "Other") ? RestoreFailed() : new GroupBuild(true, null, null, [e1]));
        var s = NewService();
        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));

        var other = await s.GetAsync(["Other/Other.csproj"], LongWait, null, Ct);
        other = await s.GetAsync(["Other/Other.csproj"], LongWait, null, Ct); // its one retry, spent
        var app = await s.GetAsync(["App/App.csproj"], LongWait, null, Ct);

        Assert.Equal(BaselineStatus.Failed, other.Status);
        Assert.Equal(BaselineStatus.Ready, app.Status);
        Assert.Null(app.FailureReason);
        Assert.True(app.Build!.Verifiable);
        Assert.Equal([e1], app.Build.Errors);
    }

    [Fact]
    public async Task Failed_ThenOtherProjectRequested_ReturnsReady()
    {
        TwoProjectRepo();
        // The prebuild builds App and Lib together and fails because of Lib.
        _verifier.Handler = (p, _) => Task.FromResult(Has(p, "Lib") ? RestoreFailed() : new GroupBuild(true, null, null, []));
        var s = NewService();
        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));
        Assert.Equal(BaselineStatus.Failed, s.Last!.Status);
        Assert.Equal(BaselineStatus.Failed, (await s.GetAsync(["Lib/Lib.csproj"], LongWait, null, Ct)).Status);

        var app = await s.GetAsync(["App/App.csproj"], LongWait, null, Ct);

        Assert.Equal(BaselineStatus.Ready, app.Status);
        Assert.Equal(3, _verifier.Calls);
        Assert.Equal([Path.Combine(_mirror, "App", "App.csproj")], _verifier.Requests[2]);
    }

    [Fact]
    public async Task RetryIsPerProject()
    {
        TwoProjectRepo();
        _verifier.Handler = (_, _) => Task.FromResult(RestoreFailed());
        var s = NewService();
        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));
        Assert.Equal(1, _verifier.Calls);

        Assert.Equal(BaselineStatus.Failed, (await s.GetAsync(["App/App.csproj"], LongWait, null, Ct)).Status);
        Assert.Equal(2, _verifier.Calls);
        Assert.Equal(BaselineStatus.Failed, (await s.GetAsync(["Lib/Lib.csproj"], LongWait, null, Ct)).Status);
        Assert.Equal(3, _verifier.Calls);
        Assert.Equal(BaselineStatus.Failed, (await s.GetAsync(["App/App.csproj"], LongWait, null, Ct)).Status);
        Assert.Equal(BaselineStatus.Failed, (await s.GetAsync(["Lib/Lib.csproj"], LongWait, null, Ct)).Status);
        Assert.Equal(3, _verifier.Calls);
    }

    [Fact]
    public async Task StaleFailure_AfterWaitingOnUnrelatedJob_UsesRetry()
    {
        TwoProjectRepo();
        Write("Other/Other.csproj", Csproj);
        Commit();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _verifier.Handler = async (p, _) =>
        {
            if (Has(p, "Other")) await gate.Task;
            return Has(p, "Lib") ? RestoreFailed() : new GroupBuild(true, null, null, []);
        };
        var s = NewService();
        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));
        Assert.Equal(1, _verifier.Calls); // the prebuild failed because of Lib

        var other = s.GetAsync(["Other/Other.csproj"], LongWait, null, Ct);
        await WaitUntil(() => _verifier.Calls == 2);
        var lib = s.GetAsync(["Lib/Lib.csproj"], LongWait, null, Ct); // waits on Other's job first
        await Task.Delay(50, Ct);
        gate.SetResult();

        Assert.Equal(BaselineStatus.Ready, (await other).Status);
        Assert.Equal(BaselineStatus.Failed, (await lib).Status);
        Assert.Equal(3, _verifier.Calls); // Lib's own retry ran
        Assert.Equal([Path.Combine(_mirror, "Lib", "Lib.csproj")], _verifier.Requests[2]);
    }

    [Fact]
    public async Task NewProjectNotAtHead_EmptyBaseline_Ready()
    {
        SolutionRepo(withTests: false);
        var s = NewService();
        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));
        Write("New/New.csproj", Csproj);
        Write("New/N.cs", "class N {}");
        _verifier.Handler = (p, _) => Task.FromResult(Has(p, "New") ? RestoreFailed() : new GroupBuild(true, null, null, []));

        var b = await s.GetAsync(["New/New.csproj", "App/App.csproj"], LongWait, null, Ct);

        Assert.Equal(BaselineStatus.Ready, b.Status);
        Assert.True(b.Build!.Verifiable);
        Assert.Empty(b.Build.Errors);
        Assert.Equal(1, _verifier.Calls);
    }

    [Fact]
    public async Task EnvironmentFailure_Failed_NoBackgroundRetry()
    {
        SolutionRepo();
        _verifier.Handler = (_, _) => Task.FromResult(RestoreFailed());
        var s = NewService();

        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));
        var polls = s.PollCount;
        await WaitUntil(() => s.PollCount >= polls + 3, () => _time.Advance(Poll));

        var b = Done(s);
        Assert.Equal(BaselineStatus.Failed, b.Status);
        Assert.Contains("NU1101", b.FailureReason);
        Assert.Equal(1, _verifier.Calls);
        Assert.Equal(0, _tests.Calls);
        Assert.False(File.Exists(s.CachePath));
    }

    [Fact]
    public async Task EnvironmentFailure_RetriedOnceOnVerify()
    {
        SolutionRepo();
        _verifier.Handler = (_, _) => Task.FromResult(RestoreFailed());
        var s = NewService();
        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));
        Assert.Equal(1, _verifier.Calls);

        var first = await s.GetAsync(["App/App.csproj"], LongWait, null, Ct);
        Assert.Equal(BaselineStatus.Failed, first.Status);
        Assert.Equal(2, _verifier.Calls);

        var second = await s.GetAsync(["App/App.csproj"], LongWait, null, Ct);
        Assert.Equal(BaselineStatus.Failed, second.Status);
        Assert.Contains("NU1101", second.FailureReason);
        Assert.Equal(2, _verifier.Calls);
    }

    [Fact]
    public async Task TestHostCrash_IsEnvironmentFailure_BuildKept()
    {
        SolutionRepo();
        _tests.Handler = (_, _) => Task.FromResult(new TestRun([], [], 1, false, false,
            $"{TestRunner.ReportNotWritten} (종료 코드 1)", ReportMissing: true));
        var s = NewService();

        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));

        var b = Done(s);
        Assert.Equal(BaselineStatus.Failed, b.Status);
        Assert.Contains(TestRunner.ReportNotWritten, b.FailureReason);
        Assert.True(b.Build!.Verifiable);
        Assert.False(File.Exists(s.CachePath));
    }

    [Fact]
    public async Task ShutdownMidBuild_NoCacheWritten()
    {
        SolutionRepo();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _verifier.Handler = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new GroupBuild(true, null, null, []);
        };
        var s = NewService();

        await s.StartAsync(Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await s.StopAsync(Ct);

        Assert.False(File.Exists(s.CachePath));
        Assert.NotEqual(BaselineStatus.Ready, s.Last?.Status);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_mirror)!, "*.json"));
    }

    [Fact]
    public async Task Ready_Cached_ReusedAfterRestart()
    {
        SolutionRepo();
        var first = NewService();
        await first.StartAsync(Ct);
        await WaitUntil(() => Settled(first));
        await first.StopAsync(Ct);
        Assert.Equal(1, _verifier.Calls);

        var second = NewService();
        var b = await second.GetAsync(["App/App.csproj"], LongWait, null, Ct);

        Assert.Equal(BaselineStatus.Ready, b.Status);
        Assert.Equal(1, _verifier.Calls);
        Assert.Equal(1, _tests.Calls);
        Assert.True(b.Tests.ContainsKey("App.Tests/App.Tests.csproj"));
    }

    [Fact]
    public async Task ProjectOutsidePrebuild_BuiltAndMerged()
    {
        PlainRepo();
        Write("Other/Other.csproj", Csproj);
        Write("Shop.slnx", "<Solution><Project Path=\"App/App.csproj\" /></Solution>");
        Commit();
        var e1 = new Diagnostic(Path.Combine(_mirror, "App", "A.cs"), 1, 1, "error", "CS1", "a");
        var e2 = new Diagnostic(Path.Combine(_mirror, "Other", "O.cs"), 1, 1, "error", "CS2", "o");
        _verifier.Handler = (p, _) => Task.FromResult(new GroupBuild(true, null, null,
            p.Any(x => x.Contains("Other", StringComparison.Ordinal)) ? [e2] : [e1]));
        var s = NewService();
        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));

        var b = await s.GetAsync(["Other/Other.csproj"], LongWait, null, Ct);

        Assert.Equal(BaselineStatus.Ready, b.Status);
        Assert.Equal(2, _verifier.Calls);
        Assert.Equal([Path.Combine(_mirror, "Other", "Other.csproj")], _verifier.Requests[1]);
        Assert.Equal([e1, e2], b.Build!.Errors);
    }

    [Fact]
    public async Task NoSolution_NoPrebuild()
    {
        PlainRepo();
        var s = NewService();

        await s.StartAsync(Ct);
        await WaitUntil(() => s.PollCount >= 2, () => _time.Advance(Poll));

        Assert.Equal(0, _verifier.Calls);
        Assert.Null(s.Last);
    }

    [Fact]
    public async Task NoTestProjects_TestsEmpty()
    {
        SolutionRepo(withTests: false);
        var s = NewService();

        await s.StartAsync(Ct);
        await WaitUntil(() => Settled(s));

        var b = Done(s);
        Assert.Equal(BaselineStatus.Ready, b.Status);
        Assert.Empty(b.Tests);
        Assert.Empty(b.TestProjectsRun);
        Assert.Equal(0, _tests.Calls);
    }

    [Fact]
    public async Task SlowBuild_ReturnsBuilding()
    {
        PlainRepo();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _verifier.Handler = async (_, _) => { await gate.Task; return new GroupBuild(true, null, null, []); };
        var s = NewService();

        var pending = s.GetAsync(["App/App.csproj"], TimeSpan.FromSeconds(5), null, Ct);
        await WaitUntil(() => _verifier.Calls == 1);
        _time.Advance(TimeSpan.FromSeconds(6));
        var b = await pending;
        Assert.Equal(BaselineStatus.Building, b.Status);

        gate.SetResult();
        var after = await s.GetAsync(["App/App.csproj"], LongWait, null, Ct);
        Assert.Equal(BaselineStatus.Ready, after.Status);
        Assert.Equal(1, _verifier.Calls);
    }

    private sealed class FakeVerifier : IGroupVerifier
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<IReadOnlyList<string>> Requests { get; } = [];
        public List<string> Roots { get; } = [];
        public Func<IReadOnlyList<string>, CancellationToken, Task<GroupBuild>> Handler { get; set; } =
            (_, _) => Task.FromResult(new GroupBuild(true, null, null, []));

        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct)
        {
            lock (Requests) { Requests.Add(projects); Roots.Add(ws.Root); }
            Interlocked.Increment(ref _calls);
            return Handler(projects, ct);
        }
    }

    private sealed class FakeTests : IBaselineTestRunner
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<string> Projects { get; } = [];
        public Func<string, CancellationToken, Task<TestRun>> Handler { get; set; } =
            (_, _) => Task.FromResult(new TestRun([new TestCaseResult("T.Ok", true, null)], [], 0, false, true, null));

        public Task<TestRun> RunAsync(string testProject, string workDir, string trxPath, CancellationToken ct)
        {
            lock (Projects) Projects.Add(testProject);
            Interlocked.Increment(ref _calls);
            return Handler(testProject, ct);
        }
    }
}
