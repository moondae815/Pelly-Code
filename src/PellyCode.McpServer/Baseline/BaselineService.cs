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
    private long _failures; // generation of the last recorded failure; guarded by _gate

    /// <summary>The HEAD mirror folder: baseline build errors carry paths inside it.</summary>
    public string MirrorDir => mirrorDir;

    /// <summary>The cache file beside the mirror folder: <c>&lt;mirror parent&gt;/&lt;hash&gt;.baseline.json</c>.</summary>
    internal string CachePath { get; } = CacheFileFor(fs, mirrorDir);

    /// <summary>Completed background HEAD checks (tests advance a fake clock until it moves instead of sleeping).</summary>
    internal int PollCount => Volatile.Read(ref _polls);

    /// <summary>The state of the last baseline work over every project, for <c>--check</c>; null before any.</summary>
    public Baseline? Last
    {
        get
        {
            lock (_gate)
                return _state is null ? null : Snapshot(_state, null, _inflight.IsCompleted ? null : BaselineStatus.Building);
        }
    }

    /// <summary>
    /// A baseline of exactly <paramref name="projectsRelative"/> (root-relative .csproj paths) at the current HEAD: Ready when all of
    /// them are built (a project HEAD does not have counts as an empty baseline), Failed only when one of them (or one of its test
    /// projects) could not be built or run after its one retry. Waits for work in flight up to <paramref name="wait"/>, then returns
    /// <see cref="BaselineStatus.Building"/> while the work goes on. <paramref name="ct"/> cancels only this wait, never the shared work.
    /// </summary>
    public async Task<Baseline> GetAsync(IReadOnlyList<string> projectsRelative, TimeSpan wait, IProgress<string>? progress, CancellationToken ct)
    {
        var head = await CheckHeadAsync(ct);
        if (head is null) return new Baseline("", BaselineStatus.Failed, null, Empty, [], NoCommit, time.GetUtcNow());

        var wanted = projectsRelative.Select(Normalize).Distinct(PathComparers.ForOs).ToList();
        var deadline = time.GetUtcNow() + wait;
        long since;
        lock (_gate) since = _failures; // a failure recorded after this is one this request saw happen
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
                else if (s is not null && wanted.All(s.Covers))
                {
                    return Snapshot(s, wanted, null);
                }
                else if (s is not null && s.FailedKeys(wanted) is { Count: > 0 } failed
                         && failed.Any(k => s.FailedGeneration[k] > since || s.RetryUsed.Contains(k)))
                {
                    // One of its projects failed after this request started (a fresh attempt), or that project's one retry is spent.
                    return Snapshot(s, wanted, null);
                }
                else if (_stop.IsCancellationRequested)
                {
                    // Shutting down: no new work would ever finish.
                    return new Baseline(head, BaselineStatus.None, null, Empty, [], null, time.GetUtcNow());
                }
                else
                {
                    if (s is not null)
                        foreach (var key in s.FailedKeys(wanted))
                        {
                            // The one retry a request gets for this project at this HEAD.
                            s.RetryUsed.Add(key);
                            s.Failures.Remove(key);
                            s.FailedGeneration.Remove(key);
                            s.FailedBuilds.Remove(key);
                        }
                    job = _inflight = Schedule(head, wanted, prebuild: false);
                }
            }

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
                if (_state?.Head == head)
                {
                    var reason = $"HEAD 기준 작업이 실패했습니다: {e.Message}";
                    if (prebuild) _state.GeneralFailure ??= reason;
                    foreach (var p in projects.Where(p => !_state.Covers(p))) Record(_state, p, reason);
                }
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
            if (prebuild ? s.SolutionBuilt : requested.All(p => s.Covers(p) || s.Failures.ContainsKey(p))) return;
            if (prebuild) s.GeneralFailure = null;
        }
        var failed = false;

        // Records the failure on each key (a project or a test project); null records it for the prebuild as a whole.
        void FailAll(IReadOnlyList<string>? projects, string reason)
        {
            failed = true;
            lock (_gate)
            {
                if (projects is null) s.GeneralFailure = reason;
                else foreach (var p in projects) Record(s, p, reason);
                s.At = time.GetUtcNow();
            }
            logger.LogWarning("HEAD {Head} 기준을 만들지 못했습니다: {Reason}", Short(head), reason);
        }

        // A git or mirror failure concerns every requested project that still needs work.
        List<string>? pending;
        lock (_gate) pending = prebuild ? null : requested.Where(p => !s.Covers(p)).ToList();
        IReadOnlyList<GitChange> changes;
        try
        {
            changes = await git.ChangesAsync(ct);
        }
        catch (InvalidOperationException e)
        {
            FailAll(pending, e.Message);
            return;
        }
        var synced = await _mirror.SyncAsync(root, mirrorDir, changes, ct);
        if (!synced.Ok)
        {
            FailAll(pending, synced.FailureReason!);
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
            FailAll(null, $"솔루션을 읽지 못했습니다: {e.Message}");
            return;
        }

        // A project HEAD does not have (new in the working tree) has an empty baseline: nothing to build.
        List<string> missing;
        lock (_gate)
        {
            foreach (var p in projects.Where(p => !s.Built.Contains(p) && !fs.File.Exists(Full(p)))) s.Absent.Add(p);
            missing = projects.Where(p => !s.Built.Contains(p) && !s.Absent.Contains(p) && !s.Failures.ContainsKey(p)).ToList();
        }
        if (missing.Count > 0)
        {
            logger.LogInformation("HEAD 기준 빌드: 프로젝트 {Count}개", missing.Count);
            var build = await verifier.BuildAsync(mirrorWs, missing.Select(Full).ToList(), ct);
            if (!build.Verifiable)
            {
                // Restore failure, missing MSBuild, timeout…: the environment, not HEAD's code. Each project of the group keeps it
                // until its own retry (a retry builds that project alone).
                FailAll(missing, build.Reason ?? "HEAD 기준 빌드를 할 수 없습니다");
                lock (_gate)
                    foreach (var p in missing) s.FailedBuilds[p] = build;
            }
            else
            {
                lock (_gate)
                {
                    foreach (var e in build.Errors)
                        if (!s.Errors.Contains(e)) s.Errors.Add(e);
                    foreach (var p in missing) s.Built.Add(p);
                }
            }
        }

        // The test projects of every built project of this job; each is run once and fails on its own.
        List<string> built;
        lock (_gate) built = projects.Where(p => s.Built.Contains(p) && !s.Failures.ContainsKey(p)).ToList();
        foreach (var p in built)
        {
            bool known;
            lock (_gate) known = s.TestsOf.ContainsKey(p);
            if (known) continue;
            try
            {
                var found = TestProjectLocator.Find(fs, mirrorDir, Full(p), testOptions).Select(Relative).Distinct(PathComparers.ForOs).ToList();
                lock (_gate) s.TestsOf[p] = found;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                FailAll([p], e.Message);
            }
        }

        List<string> toRun;
        lock (_gate)
            toRun = built.Where(s.TestsOf.ContainsKey).SelectMany(p => s.TestsOf[p]).Distinct(PathComparers.ForOs)
                .Where(t => !s.TestProjectsRun.Contains(t, StringComparer.Ordinal) && !s.Failures.ContainsKey(t)).ToList();
        foreach (var rel in toRun)
        {
            logger.LogInformation("HEAD 기준 테스트: {Project}", rel);
            var trx = fs.Path.Combine(mirrorWs.PellyDir, "baseline", $"test-{Interlocked.Increment(ref _trx)}.trx");
            var run = await tests.RunAsync(Full(rel), mirrorDir, trx, ct);
            if (TestFailure(run) is { } reason)
            {
                FailAll([rel], $"HEAD 기준 테스트를 실행하지 못했습니다 ({rel}): {reason}");
                continue;
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
            if (prebuild && !failed) s.SolutionBuilt = true;
            s.Done = true;
            s.At = time.GetUtcNow();
        }
        // Only a job that ended without a failure is written: a failure is retried, never cached.
        if (!failed) WriteCache(s);
        logger.LogInformation("HEAD {Head} 기준 작업 끝: 빌드 오류 {Errors}건, 테스트 프로젝트 {Tests}개, 실패 {Failures}건",
            Short(head), s.Errors.Count, s.TestProjectsRun.Count, s.Failures.Count);
    }

    /// <summary>Records a failure on a key with a new generation (caller holds <see cref="_gate"/>).</summary>
    private void Record(State s, string key, string reason)
    {
        if (!s.Failures.TryAdd(key, reason)) return;
        s.FailedGeneration[key] = ++_failures;
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

    /// <summary>
    /// The baseline of <paramref name="wanted"/> (every project when null). Build errors are the merged errors of every project
    /// built so far: they cannot be told apart by project, and an error the current build does not repeat is never reported anyway.
    /// </summary>
    private static Baseline Snapshot(State s, IReadOnlyList<string>? wanted, BaselineStatus? status)
    {
        var failed = wanted is null ? s.Failures.Keys.ToList() : s.FailedKeys(wanted);
        var reasons = failed.Select(k => s.Failures[k]).Prepend(wanted is null ? s.GeneralFailure : null).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToList();
        var failedBuild = failed.Select(k => s.FailedBuilds.GetValueOrDefault(k)).OfType<GroupBuild>().FirstOrDefault();
        var anyBuilt = wanted is null ? s.Built.Count > 0 || s.Absent.Count > 0 : wanted.Any(p => s.Built.Contains(p) || s.Absent.Contains(p));
        var build = failedBuild ?? (anyBuilt || reasons.Count == 0 ? new GroupBuild(true, null, null, s.Errors.ToList()) : null);

        var testProjects = (wanted is null
                ? s.TestProjectsRun
                : wanted.Where(s.TestsOf.ContainsKey).SelectMany(p => s.TestsOf[p]).Where(s.Tests.ContainsKey))
            .Distinct(StringComparer.Ordinal).ToList();
        var settled = reasons.Count > 0 ? BaselineStatus.Failed
            : wanted is not null || s.Done ? BaselineStatus.Ready
            : BaselineStatus.None;
        return new Baseline(s.Head, status ?? settled, build,
            testProjects.ToDictionary(t => t, t => s.Tests[t], StringComparer.Ordinal), testProjects,
            reasons.Count == 0 ? null : string.Join("; ", reasons), s.At);
    }

    private static string Normalize(string rel) => rel.Replace('\\', '/').TrimStart('/');
    private string Full(string rel) => fs.Path.GetFullPath(fs.Path.Combine(mirrorDir, rel.Replace('/', fs.Path.DirectorySeparatorChar)));
    private string Relative(string full) => fs.Path.GetRelativePath(mirrorDir, full).Replace('\\', '/');
    private static string Short(string head) => head.Length > 12 ? head[..12] : head;

    // ---- disk cache: the baseline of one HEAD, written to a temp file and renamed into place after a job without failures ----

    private static string CacheFileFor(IFileSystem fs, string mirrorDir)
    {
        var dir = mirrorDir.TrimEnd('/', '\\');
        return fs.Path.Combine(fs.Path.GetDirectoryName(dir)!, fs.Path.GetFileName(dir) + ".baseline.json");
    }

    private sealed record CacheFile(int Version, string Head, bool SolutionBuilt, List<string> Built, List<string> Absent,
        List<Diagnostic> Errors, Dictionary<string, List<string>> TestsOf, Dictionary<string, TestRun> Tests,
        List<string> TestProjectsRun, DateTimeOffset At);

    private const int CacheVersion = 2;

    private void WriteCache(State s)
    {
        CacheFile data;
        lock (_gate)
            data = new CacheFile(CacheVersion, s.Head, s.SolutionBuilt, s.Built.ToList(), s.Absent.ToList(), s.Errors.ToList(),
                s.TestsOf.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal),
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
            foreach (var p in data.Absent) s.Absent.Add(p);
            s.Errors.AddRange(data.Errors);
            foreach (var (k, v) in data.TestsOf) s.TestsOf[k] = v;
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

    /// <summary>
    /// Baseline work for one HEAD; guarded by <see cref="_gate"/>. Failures are kept per key — a project, or a test project — with
    /// their one request retry, so a failure stays with what failed.
    /// </summary>
    private sealed class State(string head)
    {
        public string Head { get; } = head;
        public HashSet<string> Built { get; } = new(PathComparers.ForOs);
        /// <summary>Requested projects HEAD does not have: an empty baseline.</summary>
        public HashSet<string> Absent { get; } = new(PathComparers.ForOs);
        public List<Diagnostic> Errors { get; } = [];
        /// <summary>Built project → its test projects (all root-relative).</summary>
        public Dictionary<string, List<string>> TestsOf { get; } = new(PathComparers.ForOs);
        public Dictionary<string, TestRun> Tests { get; } = new(StringComparer.Ordinal);
        public List<string> TestProjectsRun { get; } = [];
        public Dictionary<string, string> Failures { get; } = new(PathComparers.ForOs);
        public Dictionary<string, GroupBuild> FailedBuilds { get; } = new(PathComparers.ForOs);
        /// <summary>When each failure was recorded (the service's failure generation).</summary>
        public Dictionary<string, long> FailedGeneration { get; } = new(PathComparers.ForOs);
        public HashSet<string> RetryUsed { get; } = new(PathComparers.ForOs);
        /// <summary>A prebuild failure before any project was known (mirror sync, solution read); shown by <c>Last</c> only.</summary>
        public string? GeneralFailure { get; set; }
        public bool SolutionBuilt { get; set; }
        public bool Done { get; set; }
        public DateTimeOffset At { get; set; }

        /// <summary>Absent at HEAD, or built with every test project run.</summary>
        public bool Covers(string p) =>
            Absent.Contains(p) || (Built.Contains(p) && !Failures.ContainsKey(p) && TestsOf.TryGetValue(p, out var t)
                && t.All(x => TestProjectsRun.Contains(x, StringComparer.Ordinal)));

        /// <summary>The failed keys that block <paramref name="wanted"/>: the projects themselves and their test projects.</summary>
        public List<string> FailedKeys(IReadOnlyList<string> wanted) =>
            wanted.SelectMany(p => TestsOf.TryGetValue(p, out var t) ? t.Prepend(p) : [p])
                .Where(Failures.ContainsKey).Distinct(PathComparers.ForOs).ToList();
    }
}
