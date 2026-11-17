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
using PellyCode.McpServer.Verify;

namespace PellyCode.McpServer.Tests;

/// <summary>pelly_verify over a temp git repository with fake build and test runners (the mirror and the live tree told apart by root).</summary>
public sealed class VerifyCsTests : IDisposable
{
    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    private const string TestCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\App\\App.csproj\" /></ItemGroup></Project>";

    private readonly string _dir = TempRepo.Create();
    private readonly string _mirror = Path.Combine(Path.GetTempPath(), "pelly-verify-mirror-" + Guid.NewGuid().ToString("N")[..8], "0123456789abcdef");
    private readonly FakeVerifier _verifier = new();
    private readonly FakeTests _tests = new();
    private readonly List<BaselineService> _services = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var s in _services)
        {
            try { s.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            s.Dispose();
        }
        TempRepo.Delete(_dir);
        TempRepo.Delete(Path.GetDirectoryName(_mirror)!);
    }

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(text));
    }

    private string Live(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
    private string Mirror(string rel) => Path.Combine(_mirror, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>App (A.cs) and App.Tests referencing it, committed.</summary>
    private void AppRepo(bool withTests = true, string name = "App")
    {
        Write($"{name}/{name}.csproj", Csproj);
        Write($"{name}/A.cs", "class A {}");
        Write($"{name}/B.cs", "class B {}");
        if (withTests)
        {
            Write("App.Tests/App.Tests.csproj", TestCsproj);
            Write("App.Tests/T.cs", "class T {}");
        }
        TempRepo.Commit(_dir);
    }

    private VerifyService NewService(TimeSpan? wait = null)
    {
        var fs = new FileSystem();
        var git = new GitRepo(new SystemProcessRunner(TimeProvider.System), _dir);
        var baseline = new BaselineService(fs, _dir, _mirror, git, _verifier, _tests, new TestOptions(), TimeProvider.System,
            NullLogger<BaselineService>.Instance, new NoRestore());
        _services.Add(baseline);
        var ctx = new ServerContext(_dir, new PellyOptions(), new Workspace(_dir, Path.Combine(_dir, ".pelly")));
        return new VerifyService(fs, ctx, git, baseline, _verifier, _tests, wait ?? TimeSpan.FromMinutes(5));
    }

    private static Diagnostic Error(string root, string rel, int line, string code, string message) =>
        new(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), line, 5, "error", code, message);

    private static GroupBuild Built(params Diagnostic[] errors) => new(true, null, null, errors);

    private static TestRun Run(params (string Name, bool Passed, string? Message)[] results) =>
        new(results.Select(r => new TestCaseResult(r.Name, r.Passed, r.Message)).ToList(), [],
            results.All(r => r.Passed) ? 0 : 1, false, true, null);

    private static string[] Lines(string text) => text.Split('\n');

    [Fact]
    public async Task NoChanges()
    {
        AppRepo();
        var text = await NewService().VerifyAsync(null, Ct);

        Assert.Equal("HEAD 이후 바뀐 파일이 없습니다", text);
        Assert.Equal(0, _verifier.Calls);
    }

    [Fact]
    public async Task NewBuildError_Reported_OldOneNot()
    {
        AppRepo(withTests: false);
        _verifier.Handler = (root, _, _) => Task.FromResult(root == _mirror
            ? Built(Error(_mirror, "App/A.cs", 3, "CS0103", "The name 'x' does not exist in the current context"))
            : Built(Error(_dir, "App/A.cs", 4, "CS0103", "The name 'x' does not exist in the current context"),
                Error(_dir, "App/A.cs", 7, "CS1503", "Argument 1: cannot convert from 'string' to 'int'")));
        Write("App/A.cs", "class A { void M() {} }");

        var lines = Lines(await NewService().VerifyAsync(null, Ct));

        Assert.Equal("결과: 새 오류 1건, 새 경고 0건", lines[0]);
        Assert.Contains("[빌드] App — 새 오류 1건", lines);
        Assert.Contains("- App/A.cs:7 CS1503 Argument 1: cannot convert from 'string' to 'int'", lines);
        Assert.DoesNotContain(lines, l => l.Contains("CS0103", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewFailingTest_Reported()
    {
        AppRepo();
        _tests.Handler = (_, workDir, _) => Task.FromResult(workDir == _mirror
            ? Run(("App.Tests.T.Old", true, null), ("App.Tests.T.Broken", false, "was broken"))
            : Run(("App.Tests.T.Old", false, "Expected 1 but was 2\n   at line 3"), ("App.Tests.T.Broken", false, "was broken")));
        Write("App/A.cs", "class A { int M() => 2; }");

        var lines = Lines(await NewService().VerifyAsync(null, Ct));

        Assert.Equal("결과: 새 오류 1건, 새 경고 0건", lines[0]);
        Assert.Contains("[테스트] App.Tests — 새로 실패 1건", lines);
        Assert.Contains("- App.Tests.T.Old: Expected 1 but was 2", lines);
        Assert.DoesNotContain(lines, l => l.Contains("T.Broken", StringComparison.Ordinal));
        Assert.Contains(Live("App.Tests/App.Tests.csproj"), _tests.Projects);
        Assert.Contains(Mirror("App.Tests/App.Tests.csproj"), _tests.Projects);
    }

    [Fact]
    public async Task NoTestProjects_SkipLine()
    {
        AppRepo(withTests: false, name: "Shop.Api");
        Write("Shop.Api/A.cs", "class A { int M() => 2; }");

        var lines = Lines(await NewService().VerifyAsync(null, Ct));

        Assert.Equal("결과: 새 문제 없음", lines[0]);
        Assert.Contains("[테스트] 건너뜀 — Shop.Api를 참조하는 테스트 프로젝트 없음. 동작 변경은 확인되지 않았습니다", lines);
        Assert.Equal(0, _tests.Calls);
    }

    [Fact]
    public async Task BaselineFailed_AllErrorsWithNote()
    {
        AppRepo(withTests: false);
        const string reason = "그룹 빌드 검증 불가 (App/App.csproj): NU1101 복원 실패";
        _verifier.Handler = (root, _, _) => Task.FromResult(root == _mirror
            ? new GroupBuild(false, reason, GroupVerifier.UnverifiableKind, [])
            : Built(Error(_dir, "App/A.cs", 3, "CS0103", "The name 'x' does not exist in the current context")));
        Write("App/A.cs", "class A { void M() {} }");

        var lines = Lines(await NewService().VerifyAsync(null, Ct));

        Assert.Equal("결과: 새 오류 1건, 새 경고 0건", lines[0]);
        Assert.Equal($"HEAD 기준을 만들지 못해 모든 오류를 보입니다: {reason}", lines[1]);
        Assert.Contains("- App/A.cs:3 CS0103 The name 'x' does not exist in the current context", lines);
    }

    [Fact]
    public async Task BaselineStillBuilding_ReturnsCallAgain()
    {
        AppRepo(withTests: false);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = NewService(TimeSpan.FromMilliseconds(300));
        var lockFreeWhileWaiting = true;
        _verifier.Handler = async (root, _, _) =>
        {
            if (root == _mirror)
            {
                await gate.Task;
            }
            return Built();
        };
        Write("App/A.cs", "class A { void M() {} }");

        var verify = service.VerifyAsync(new SyncProgress<string>(_ => lockFreeWhileWaiting &= service.ToolLockFree), Ct);
        var text = await verify;
        gate.SetResult();

        Assert.Equal("기준 빌드가 아직 진행 중입니다. 잠시 뒤 다시 pelly_verify를 부르세요", text);
        Assert.True(lockFreeWhileWaiting);
        Assert.DoesNotContain(_dir, _verifier.Roots);
    }

    [Fact]
    public async Task FirstLine_Counts()
    {
        AppRepo(withTests: false);
        _verifier.Handler = (root, _, _) => Task.FromResult(root == _mirror
            ? Built()
            : Built(Error(_dir, "App/A.cs", 3, "CS0103", "x"), Error(_dir, "App/B.cs", 4, "CS0029", "y")));
        Write("App/A.cs", "class A { void M() {} }");

        var lines = Lines(await NewService().VerifyAsync(null, Ct));

        Assert.Equal("결과: 새 오류 2건, 새 경고 0건", lines[0]);
    }

    [Fact]
    public async Task Over20_Truncated()
    {
        AppRepo(withTests: false);
        var errors = Enumerable.Range(1, 25).Select(i => Error(_dir, "App/A.cs", i, "CS0103", $"name{i}")).ToArray();
        _verifier.Handler = (root, _, _) => Task.FromResult(root == _mirror ? Built() : Built(errors));
        Write("App/A.cs", "class A { void M() {} }");

        var lines = Lines(await NewService().VerifyAsync(null, Ct));

        Assert.Equal("결과: 새 오류 25건, 새 경고 0건", lines[0]);
        Assert.Contains("[빌드] App — 새 오류 25건", lines);
        Assert.Equal(20, lines.Count(l => l.StartsWith("- App/A.cs:", StringComparison.Ordinal)));
        Assert.Contains("- App/A.cs:20 CS0103 name20", lines);
        Assert.DoesNotContain("- App/A.cs:21 CS0103 name21", lines);
        Assert.Contains("외 5건", lines);
    }

    [Fact]
    public async Task Verify_DeletedCs_BuildsOwningProject()
    {
        AppRepo(withTests: false);
        File.Delete(Live("App/B.cs"));

        var text = await NewService().VerifyAsync(null, Ct);

        Assert.Equal("결과: 새 문제 없음", Lines(text)[0]);
        Assert.Contains(_verifier.Requests, r => r.Root == _dir && r.Projects.SequenceEqual([Live("App/App.csproj")]));
        Assert.Contains(_verifier.Requests, r => r.Root == _mirror && r.Projects.SequenceEqual([Mirror("App/App.csproj")]));
        Assert.Contains("[빌드] App — 통과", Lines(text));
    }

    [Fact]
    public async Task Cancel_StopsLiveBuild_NotBaseline()
    {
        AppRepo(withTests: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _verifier.Handler = async (root, _, ct) =>
        {
            if (root == _dir)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return Built();
        };
        Write("App/A.cs", "class A { void M() {} }");
        var service = NewService();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var verify = service.VerifyAsync(null, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verify);
        Assert.True(service.ToolLockFree);
        var baseline = await _services[0].GetAsync(["App/App.csproj"], TimeSpan.FromSeconds(5), null, Ct);
        Assert.Equal(BaselineStatus.Ready, baseline.Status);
    }

    [Fact]
    public async Task Heartbeat_ReportsEvery10Seconds()
    {
        var time = new FakeTimeProvider();
        var sent = new List<(float Progress, string? Message)>();
        var sink = new SyncProgress<ModelContextProtocol.ProgressNotificationValue>(v => { lock (sent) sent.Add((v.Progress, v.Message)); });

        await using (var beat = new ProgressHeartbeat(sink, time, VerifyService.ProgressInterval))
        {
            beat.Report("HEAD 기준을 만드는 중입니다");
            time.Advance(TimeSpan.FromSeconds(10));
            time.Advance(TimeSpan.FromSeconds(10));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline) { lock (sent) if (sent.Count >= 3) break; await Task.Delay(10, Ct); }
        }

        lock (sent)
        {
            Assert.True(sent.Count >= 3, $"sent {sent.Count}");
            Assert.All(sent, s => Assert.Equal("HEAD 기준을 만드는 중입니다", s.Message));
            Assert.Equal(sent.Select(s => s.Progress).Order(), sent.Select(s => s.Progress)); // increasing
            Assert.Equal(sent.Count, sent.Select(s => s.Progress).Distinct().Count());
        }
        Assert.Equal(TimeSpan.FromSeconds(600), VerifyService.DefaultWaitLimit);
        Assert.Equal(TimeSpan.FromSeconds(45), VerifyService.FallbackWaitLimit);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeVerifier : IGroupVerifier
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<(string Root, IReadOnlyList<string> Projects)> Requests { get; } = [];
        public List<string> Roots { get { lock (Requests) return Requests.Select(r => r.Root).ToList(); } }
        public Func<string, IReadOnlyList<string>, CancellationToken, Task<GroupBuild>> Handler { get; set; } =
            (_, _, _) => Task.FromResult(new GroupBuild(true, null, null, []));

        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct)
        {
            lock (Requests) Requests.Add((ws.Root, projects));
            Interlocked.Increment(ref _calls);
            return Handler(ws.Root, projects, ct);
        }
    }

    private sealed class FakeTests : IBaselineTestRunner
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<string> Projects { get; } = [];
        public Func<string, string, CancellationToken, Task<TestRun>> Handler { get; set; } =
            (_, _, _) => Task.FromResult(new TestRun([new TestCaseResult("T.Ok", true, null)], [], 0, false, true, null));

        public Task<TestRun> RunAsync(string testProject, string workDir, string trxPath, CancellationToken ct)
        {
            lock (Projects) Projects.Add(testProject);
            Interlocked.Increment(ref _calls);
            return Handler(testProject, workDir, ct);
        }
    }

    private sealed class NoRestore : IBaselineRestorer
    {
        public Task<string?> RestoreAsync(string project, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
