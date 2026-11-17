using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PellyCode.Core.Builds;
using PellyCode.Core.Config;
using PellyCode.Core.Files;
using PellyCode.Core.Models;
using PellyCode.Core.Specs;
using PellyCode.Core.Testing;
using PellyCode.Core.Verification;
using PellyCode.Core.Workspaces;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Baseline;

public enum BaselineStatus { None, Building, Ready, Failed }

/// <summary>
/// The HEAD baseline: build errors and test runs of the HEAD mirror. <see cref="Build"/> holds the merged errors of every project
/// built so far (paths inside the mirror); <see cref="Tests"/> is keyed by the test project's root-relative path ('/').
/// <see cref="BaselineStatus.Failed"/> is an environment failure (<see cref="FailureReason"/>); when only the tests could not run,
/// <see cref="Build"/> is still the verifiable build. HEAD compile errors and failing HEAD tests make a <see cref="BaselineStatus.Ready"/> baseline.
/// </summary>
public sealed record Baseline(string HeadSha, BaselineStatus Status, GroupBuild? Build, IReadOnlyDictionary<string, TestRun> Tests,
    IReadOnlyList<string> TestProjectsRun, string? FailureReason, DateTimeOffset At);

/// <summary>Runs one test project for the baseline (the real one resolves the build tool and calls Core's <see cref="TestRunner"/>).</summary>
public interface IBaselineTestRunner
{
    Task<TestRun> RunAsync(string testProject, string workDir, string trxPath, CancellationToken ct);
}

/// <summary>
/// Keeps the HEAD baseline ready (design §7 「HEAD 기준 사본」, §11): prebuilds the root solution's projects in the mirror at start
/// and whenever HEAD changes (polled every 30 s and on every <see cref="GetAsync"/>), shares one build between concurrent
/// requests, builds projects outside the prebuild on demand and merges them, and caches a complete baseline on disk per HEAD.
/// An environment failure is not retried in the background, only once per HEAD on a request. Never takes the tool lock.
/// </summary>
public sealed class BaselineService(IFileSystem fs, string root, string mirrorDir, GitRepo git, IGroupVerifier verifier,
    IBaselineTestRunner tests, TestOptions testOptions, TimeProvider time, ILogger<BaselineService> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Reason when HEAD has no commit yet.</summary>
    public const string NoCommit = "HEAD 커밋이 없어 기준을 만들 수 없습니다";

    private static readonly IReadOnlyDictionary<string, TestRun> Empty = new Dictionary<string, TestRun>();

    private readonly BaseMirror _mirror = new(fs, git);
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private State? _state;
    private string? _seenHead;
    private Task _inflight = Task.CompletedTask;
    private int _polls;
    private int _trx;

    /// <summary>The cache file beside the mirror folder: <c>&lt;mirror parent&gt;/&lt;hash&gt;.baseline.json</c>.</summary>
    internal string CachePath { get; } = CacheFileFor(fs, mirrorDir);

    /// <summary>Completed background HEAD checks (tests advance a fake clock until it moves instead of sleeping).</summary>
    internal int PollCount => Volatile.Read(ref _polls);

    /// <summary>The state of the last baseline work, for <c>--check</c>; null before any.</summary>
    public Baseline? Last
    {
        get
        {
            lock (_gate)
                return _state is null ? null : Snapshot(_state, _inflight.IsCompleted ? null : BaselineStatus.Building);
        }
    }

    /// <summary>
    /// A baseline covering <paramref name="projectsRelative"/> (root-relative .csproj paths) at the current HEAD. Waits for work in
    /// flight up to <paramref name="wait"/>, then returns <see cref="BaselineStatus.Building"/> while the work goes on.
    /// <paramref name="ct"/> cancels only this wait, never the shared work.
    /// </summary>
    public async Task<Baseline> GetAsync(IReadOnlyList<string> projectsRelative, TimeSpan wait, IProgress<string>? progress, CancellationToken ct)
    {
        var head = await CheckHeadAsync(ct);
        if (head is null) return new Baseline("", BaselineStatus.Failed, null, Empty, [], NoCommit, time.GetUtcNow());

        var wanted = projectsRelative.Select(Normalize).Distinct(PathComparers.ForOs).ToList();
        var deadline = time.GetUtcNow() + wait;
        var waited = false;
        while (true)
        {
            Task job;
            lock (_gate)
            {
                var s = _state is not null && _state.Head == head ? _state : null;
                if (!_inflight.IsCompleted)
                {
                    job = _inflight;
                }
                else if (s?.Failure is not null && (waited || s.RequestRetryUsed))
                {
                    // It failed while this request waited, or the one retry a request gets for this HEAD is spent.
                    return Snapshot(s, null);
                }
                else if (s is { Failure: null, Done: true } && wanted.All(s.Built.Contains))
                {
                    return Snapshot(s, null);
                }
                else if (_stop.IsCancellationRequested)
                {
                    // Shutting down: no new work would ever finish.
                    return s is null ? new Baseline(head, BaselineStatus.None, null, Empty, [], null, time.GetUtcNow()) : Snapshot(s, null);
                }
                else
                {
                    if (s?.Failure is not null) s.RequestRetryUsed = true;
                    job = _inflight = Schedule(head, wanted, prebuild: false);
                }
            }

            waited = true;
            progress?.Report("HEAD 기준을 만드는 중입니다");
            var left = deadline - time.GetUtcNow();
            try
            {
                if (left <= TimeSpan.Zero) throw new TimeoutException();
                await job.WaitAsync(left, time, ct);
            }
            catch (TimeoutException)
            {
                return new Baseline(head, BaselineStatus.Building, null, Empty, [], null, time.GetUtcNow());
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _stop.Token);
        var ct = linked.Token;
        try
        {
            await CheckHeadAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, time, ct);
                await CheckHeadAsync(ct);
                Interlocked.Increment(ref _polls);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Stops the poll and cancels the baseline work in flight; an unfinished baseline is never cached.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync();
        await base.StopAsync(cancellationToken);
        Task inflight;
        lock (_gate) inflight = _inflight;
        try
        {
            await inflight.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override void Dispose()
    {
        _stop.Cancel();
        base.Dispose();
    }

    /// <summary>Reads HEAD and, when it is not the one last seen, starts a prebuild (only with a root solution). Returns HEAD.</summary>
    private async Task<string?> CheckHeadAsync(CancellationToken ct)
    {
        string? head;
        try
        {
            head = await git.HeadAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("HEAD를 읽지 못했습니다: {Message}", e.Message);
            return null;
        }
        if (head is null) return null;
        lock (_gate)
        {
            if (head == _seenHead) return head;
            _seenHead = head;
            if (Solutions(root).Count == 0)
            {
                logger.LogInformation("루트에 솔루션이 없어 HEAD 기준을 미리 만들지 않습니다");
                return head;
            }
            logger.LogInformation("HEAD {Head}: 기준을 미리 만듭니다", Short(head));
            _inflight = Schedule(head, [], prebuild: true);
        }
        return head;
    }

    /// <summary>Queues one job behind the ones before it (one at a time). The task never faults.</summary>
    private Task Schedule(string head, IReadOnlyList<string> projects, bool prebuild) => Task.Run(async () =>
    {
        var ct = _stop.Token;
        try
        {
            await _work.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await RunAsync(head, projects, prebuild, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("서버가 꺼져 HEAD 기준 작업을 멈췄습니다 (캐시에 쓰지 않음)");
        }
        catch (Exception e)
        {
            logger.LogError(e, "HEAD 기준 작업이 실패했습니다");
            lock (_gate)
                if (_state?.Head == head) _state.Failure ??= $"HEAD 기준 작업이 실패했습니다: {e.Message}";
        }
        finally
        {
            _work.Release();
        }
    });

    private async Task RunAsync(string head, IReadOnlyList<string> requested, bool prebuild, CancellationToken ct)
    {
        State s;
        lock (_gate)
        {
            if (_state is null || _state.Head != head) _state = LoadCache(head) ?? new State(head);
            s = _state;
            if (s is { Failure: null, Done: true } && (prebuild ? s.SolutionBuilt : requested.All(s.Built.Contains))) return;
            s.Failure = null;
            s.FailedBuild = null;
            s.Done = false;
        }

        IReadOnlyList<GitChange> changes;
        try
        {
            changes = await git.ChangesAsync(ct);
        }
        catch (InvalidOperationException e)
        {
            Fail(s, e.Message);
            return;
        }
        var synced = await _mirror.SyncAsync(root, mirrorDir, changes, ct);
        if (!synced.Ok)
        {
            Fail(s, synced.FailureReason!);
            return;
        }
        logger.LogInformation("HEAD 기준 사본을 맞췄습니다 (복사 {Copied}, 삭제 {Deleted})", synced.CopiedFiles, synced.DeletedFiles);

        var mirrorWs = new Workspace(mirrorDir, fs.Path.Combine(mirrorDir, ".pelly"));
        List<string> projects;
        try
        {
            // The prebuild reads the solution as HEAD has it (in the mirror).
            projects = prebuild
                ? Solutions(mirrorDir).SelectMany(sln => SolutionProjects.Read(fs, sln))
                    .Where(fs.File.Exists).Select(Relative).Distinct(PathComparers.ForOs).ToList()
                : requested.ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Fail(s, $"솔루션을 읽지 못했습니다: {e.Message}");
            return;
        }

        List<string> missing;
        lock (_gate) missing = projects.Where(p => !s.Built.Contains(p)).ToList();
        if (missing.Count > 0)
        {
            logger.LogInformation("HEAD 기준 빌드: 프로젝트 {Count}개", missing.Count);
            var build = await verifier.BuildAsync(mirrorWs, missing.Select(Full).ToList(), ct);
            if (!build.Verifiable)
            {
                // Restore failure, missing MSBuild, timeout…: the environment, not HEAD's code.
                lock (_gate) s.FailedBuild = build;
                Fail(s, build.Reason ?? "HEAD 기준 빌드를 할 수 없습니다");
                return;
            }
            lock (_gate)
            {
                foreach (var e in build.Errors)
                    if (!s.Errors.Contains(e)) s.Errors.Add(e);
                foreach (var p in missing) s.Built.Add(p);
            }
        }

        List<string> testProjects;
        try
        {
            testProjects = TestProjects(projects);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(s, e.Message);
            return;
        }

        foreach (var rel in testProjects)
        {
            lock (_gate)
                if (s.TestProjectsRun.Contains(rel, StringComparer.Ordinal)) continue;
            logger.LogInformation("HEAD 기준 테스트: {Project}", rel);
            var trx = fs.Path.Combine(mirrorWs.PellyDir, "baseline", $"test-{Interlocked.Increment(ref _trx)}.trx");
            var run = await tests.RunAsync(Full(rel), mirrorDir, trx, ct);
            if (TestFailure(run) is { } reason)
            {
                Fail(s, $"HEAD 기준 테스트를 실행하지 못했습니다 ({rel}): {reason}");
                return;
            }
            lock (_gate)
            {
                s.Tests[rel] = run;
                s.TestProjectsRun.Add(rel);
            }
        }

        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (prebuild) s.SolutionBuilt = true;
            s.Done = true;
            s.At = time.GetUtcNow();
        }
        WriteCache(s);
        logger.LogInformation("HEAD {Head} 기준 준비됨: 빌드 오류 {Errors}건, 테스트 프로젝트 {Tests}개",
            Short(head), s.Errors.Count, s.TestProjectsRun.Count);
    }

    /// <summary>
    /// Why a baseline test run cannot serve as the baseline — the same rules as <see cref="GroupTester"/>'s baseline: a timeout,
    /// a run that produced no results (crashed test host, missing tool or <c>test.command</c>), or a test build stopped by a
    /// non-compiler error. A test project whose HEAD code does not compile, and failing HEAD tests, are a valid baseline.
    /// </summary>
    private string? TestFailure(TestRun run)
    {
        if (run.TimedOut) return TestStep.TimedOutMessage(testOptions);
        if (run.Error is { } error) return error;
        if (run.BuildErrors.FirstOrDefault(e => !BuildMessages.IsCompilerError(e)) is { } blocker)
            return BuildMessages.Unverifiable(blocker, "테스트 프로젝트를 복원하고 빌드되는지 먼저 확인하세요");
        return null;
    }

    /// <summary>Test projects of <paramref name="projects"/>, root-relative: <c>test.projects</c> when set, else those referencing them.</summary>
    private List<string> TestProjects(IReadOnlyList<string> projects) =>
        projects.SelectMany(p => TestProjectLocator.Find(fs, mirrorDir, Full(p), testOptions))
            .Select(Relative).Distinct(PathComparers.ForOs).ToList();

    private void Fail(State s, string reason)
    {
        lock (_gate)
        {
            s.Failure = reason;
            s.At = time.GetUtcNow();
        }
        logger.LogWarning("HEAD {Head} 기준을 만들지 못했습니다: {Reason}", Short(s.Head), reason);
    }

    private List<string> Solutions(string dir)
    {
        try
        {
            if (!fs.Directory.Exists(dir)) return [];
            return fs.Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static Baseline Snapshot(State s, BaselineStatus? status)
    {
        var build = s.FailedBuild ?? (s.Built.Count > 0 || s.Failure is null ? new GroupBuild(true, null, null, s.Errors.ToList()) : null);
        var settled = s.Failure is not null ? BaselineStatus.Failed : s.Done ? BaselineStatus.Ready : BaselineStatus.None;
        return new Baseline(s.Head, status ?? settled, build, new Dictionary<string, TestRun>(s.Tests, StringComparer.Ordinal),
            s.TestProjectsRun.ToList(), s.Failure, s.At);
    }

    private static string Normalize(string rel) => rel.Replace('\\', '/').TrimStart('/');
    private string Full(string rel) => fs.Path.GetFullPath(fs.Path.Combine(mirrorDir, rel.Replace('/', fs.Path.DirectorySeparatorChar)));
    private string Relative(string full) => fs.Path.GetRelativePath(mirrorDir, full).Replace('\\', '/');
    private static string Short(string head) => head.Length > 12 ? head[..12] : head;

    // ---- disk cache: one complete baseline per HEAD, written to a temp file and renamed into place ----

    private static string CacheFileFor(IFileSystem fs, string mirrorDir)
    {
        var dir = mirrorDir.TrimEnd('/', '\\');
        return fs.Path.Combine(fs.Path.GetDirectoryName(dir)!, fs.Path.GetFileName(dir) + ".baseline.json");
    }

    private sealed record CacheFile(int Version, string Head, bool SolutionBuilt, List<string> Built, List<Diagnostic> Errors,
        Dictionary<string, TestRun> Tests, List<string> TestProjectsRun, DateTimeOffset At);

    private const int CacheVersion = 1;

    private void WriteCache(State s)
    {
        CacheFile data;
        lock (_gate)
            data = new CacheFile(CacheVersion, s.Head, s.SolutionBuilt, s.Built.ToList(), s.Errors.ToList(),
                new Dictionary<string, TestRun>(s.Tests, StringComparer.Ordinal), s.TestProjectsRun.ToList(), s.At);
        var temp = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            fs.File.WriteAllText(temp, JsonSerializer.Serialize(data));
            fs.File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("HEAD 기준 캐시를 쓰지 못했습니다: {Message}", e.Message);
            try
            {
                if (fs.File.Exists(temp)) fs.File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The cached baseline of <paramref name="head"/>; null when there is none, it is another HEAD's, or it cannot be read.</summary>
    private State? LoadCache(string head)
    {
        try
        {
            if (!fs.File.Exists(CachePath)) return null;
            var data = JsonSerializer.Deserialize<CacheFile>(fs.File.ReadAllText(CachePath));
            if (data is null || data.Version != CacheVersion || data.Head != head) return null;
            var s = new State(head) { SolutionBuilt = data.SolutionBuilt, Done = true, At = data.At };
            foreach (var p in data.Built) s.Built.Add(p);
            s.Errors.AddRange(data.Errors);
            foreach (var (k, v) in data.Tests) s.Tests[k] = v;
            s.TestProjectsRun.AddRange(data.TestProjectsRun);
            logger.LogInformation("HEAD {Head} 기준을 캐시에서 읽었습니다", Short(head));
            return s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger.LogWarning("HEAD 기준 캐시를 읽지 못했습니다: {Message}", e.Message);
            return null;
        }
    }

    /// <summary>Baseline work for one HEAD; guarded by <see cref="_gate"/>.</summary>
    private sealed class State(string head)
    {
        public string Head { get; } = head;
        public HashSet<string> Built { get; } = new(PathComparers.ForOs);
        public List<Diagnostic> Errors { get; } = [];
        public Dictionary<string, TestRun> Tests { get; } = new(StringComparer.Ordinal);
        public List<string> TestProjectsRun { get; } = [];
        public bool SolutionBuilt { get; set; }
        public bool Done { get; set; }
        public string? Failure { get; set; }
        public GroupBuild? FailedBuild { get; set; }
        public bool RequestRetryUsed { get; set; }
        public DateTimeOffset At { get; set; }
    }
}
