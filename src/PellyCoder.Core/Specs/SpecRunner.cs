using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>
/// <c>pelly apply</c>: checks a change spec, then runs its tasks top to bottom through <see cref="ITaskExecutor"/> under one
/// workspace lock, marking each line in the spec as it goes (design §6). A task group runs as one unit: one baseline build
/// of the affected projects, its tasks in group mode, one verification build, re-runs of the tasks the new errors belong to,
/// one approval, and one rollback of the whole group when it fails.
/// </summary>
/// <param name="pruneRuns">Run-log retention, called once at the end under the apply lock (task runs inside an apply skip it).</param>
/// <param name="groups">Builds a group's affected projects; a spec with groups exits 4 without it.</param>
/// <param name="groupFeedback">Writes a group re-run's build feedback; defaults to the default feedback budget.</param>
/// <param name="groupRetryMax">Group re-runs when the apply has no <c>--max-retries</c> (<c>retry.max</c>); defaults to its default.</param>
/// <param name="impact">Searches the grouped cs tasks' impact at the start (warnings only); no search without it.</param>
/// <param name="groupTests">Runs a group's acceptance-criterion tests; a group with linked tests exits 4 without it.</param>
public sealed class SpecRunner(IFileSystem fs, FileGuard guard, ITaskExecutor executor, IUserInteraction ui, TimeProvider time, int sqlCompatLevel,
    Action<Workspace, string>? pruneRuns = null, IGroupVerifier? groups = null, FeedbackBuilder? groupFeedback = null, int? groupRetryMax = null,
    IImpactFinder? impact = null, IGroupTester? groupTests = null)
{
    private static readonly JsonSerializerOptions SummaryJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private IFileSystem Fs => fs;
    private FileGuard Guard => guard;
    private ITaskExecutor Executor => executor;
    private IUserInteraction Ui => ui;
    private TimeProvider Time => time;
    private IGroupVerifier? Groups => groups;
    private IGroupTester? GroupTests => groupTests;
    private int GroupRetryMax => groupRetryMax ?? new RetryOptions().Max;

    private FeedbackBuilder? _defaultFeedback;
    private FeedbackBuilder GroupFeedback =>
        groupFeedback ?? (_defaultFeedback ??= new FeedbackBuilder(QwenTokenCounter.Shared, new BudgetOptions().Feedback));

    public async Task<int> RunAsync(string specPath, ApplyOptions o, CancellationToken ct)
    {
        // Static check and the cheap refusals first: the impact search can take up to the semantic load timeout.
        var check = SpecChecker.Check(fs, specPath, sqlCompatLevel);
        if (check.HasErrors)
        {
            foreach (var issue in check.Issues) ui.Warn(Format(issue));
            return 4;
        }

        // Review means a human looks at the diff; without a console the question cannot be asked at all.
        if (!ui.IsInteractive && o.ReviewAtEnd)
        {
            ui.Warn("--review-at-end는 대화형 실행에서만 쓸 수 있습니다.");
            return 4;
        }

        // Refuse before anything changes: without --yes every task would be edited and then rolled back.
        if (!ui.IsInteractive && !o.Yes && !o.DryRun)
        {
            ui.Warn("비대화형 실행에는 --yes가 필요합니다.");
            return 4;
        }

        var ws = check.Workspace;
        var held = WorkspaceLock.TryAcquire(fs, ws);
        if (held is null)
        {
            ui.Warn("다른 pelly 실행이 이 작업 공간을 쓰고 있습니다. 끝난 뒤 다시 실행하세요.");
            return 4;
        }

        ApplyRun? run = null;
        try
        {
            check = await SpecChecker.AddImpactAsync(check, impact, false, ct);
            var warnings = check.Issues.Where(i => i.Severity == SpecIssueSeverity.Warning).ToList();
            if (warnings.Count > 0)
            {
                foreach (var issue in warnings) ui.Warn(Format(issue));
                if (ui.IsInteractive && !o.Yes && !await ui.ConfirmAsync("경고가 있습니다. 계속할까요?", false, ct))
                    return 2;
            }

            run = new ApplyRun(this, ws, specPath, o, warnings, check.Impact);
            return await run.ExecuteAsync(ct);
        }
        finally
        {
            try
            {
                if (run?.ApplyId is not null) pruneRuns?.Invoke(ws, run.ApplyId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Retention is housekeeping: never changes the apply's result.
            }
            finally
            {
                held.Dispose();
            }
        }
    }

    private static string Format(SpecIssue issue)
    {
        string kind = issue.Severity == SpecIssueSeverity.Error ? "오류" : "경고";
        return issue.Line > 0 ? $"{kind} 줄 {issue.Line}: {issue.Message}" : $"{kind}: {issue.Message}";
    }

    /// <summary>State of one apply after the lock is held.</summary>
    private sealed class ApplyRun(SpecRunner owner, Workspace ws, string specPath, ApplyOptions o, IReadOnlyList<SpecIssue> warnings, ImpactResult? impact)
    {
        private readonly List<ApplyTaskResult> _results = [];
        private readonly List<string> _marked = [];
        private readonly Dictionary<string, (string RunId, string Path)> _done = new(StringComparer.Ordinal); // task ID → run and file path
        private SpecDocument _doc = null!;
        private bool _archived;

        // Acceptance-criterion tests (design §6 test-first): the links, the tasks already done when the apply started (--resume),
        // each task's first and last run in this apply, and how each task ended (a group task: its group's exit code).
        private IReadOnlyList<AcLink> _links = [];
        private HashSet<string> _doneAtStart = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RunResult> _firstRuns = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RunResult> _lastRuns = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _ends = new(StringComparer.Ordinal);
        private bool _rolledBack; // the whole apply was fully undone: end review rejected, or cancelled under --review-at-end

        // Group test-first (design §7.3): RED of the criteria whose first linked task is in a group, from the group's baseline,
        // and the expected breaks of the criteria whose last linked task is in a group, from the group's last verdict.
        private readonly Dictionary<string, RedState> _groupRed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _groupBreaks = new(StringComparer.Ordinal);

        public string? ApplyId { get; private set; }

        public async Task<int> ExecuteAsync(CancellationToken ct)
        {
            _doc = SpecDocument.Load(owner.Guard, specPath);
            var tasks = _doc.Spec.Tasks;
            _links = TestFirstLinks.Build(_doc.Spec);
            _doneAtStart = tasks.Where(t => t.Done).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            if (!o.Resume && tasks.Any(t => t.Done))
            {
                owner.Ui.Warn("이미 진행된 명세입니다. --resume을 쓰세요.");
                return 4;
            }

            int first = tasks.ToList().FindIndex(t => !t.Done);
            var todo = first < 0 ? [] : tasks.Skip(first).Where(t => !t.Done).ToList();

            if (o.DryRun) return await DryRunAsync(todo, ct);

            // Refuse before anything changes: a group cannot be verified without its build.
            if (owner.Groups is null && todo.Any(t => t.Group is not null))
            {
                owner.Ui.Warn("그룹 빌드 검증기를 사용할 수 없습니다.");
                return 4;
            }
            if (owner.GroupTests is null && todo.Any(t => t.Group is not null && TestFirstLinks.For(_links, t.Id) is not null))
            {
                owner.Ui.Warn("그룹 테스트 실행기를 사용할 수 없습니다.");
                return 4;
            }

            ApplyId = "apply-" + RunLogger.NewRunId(owner.Time);
            if (!await SpecWrittenAsync(_doc.SetStatusAsync(SpecStatus.InProgress, CancellationToken.None))) return Finish(4);

            var tx = new ApplyTransaction(owner.Guard, ws, ApplyId);
            var taskOptions = new TaskRunOptions(o.Yes || o.ReviewAtEnd, false, o.MaxRetries, o.Verbose);
            int? stopCode = null;
            bool anyFailed = false;

            for (int i = 0; i < todo.Count; i++)
            {
                var task = todo[i];
                if (ct.IsCancellationRequested) return Finish(await CancelledAsync(tx));

                if (task.Group is { } groupId)
                {
                    // The parser keeps a group's tasks contiguous; they run as one unit.
                    var group = todo.Skip(i).TakeWhile(t => t.Group == groupId).ToList();
                    i += group.Count - 1;
                    var end = await RunGroupAsync(tx, group, ct);
                    foreach (var t in group) _ends[t.Id] = end.ExitCode;
                    if (end.Terminal) return Finish(end.ExitCode);
                    if (end.ExitCode == 0) continue;

                    anyFailed = true;
                    // A declined test that already passes means the spec's criterion is wrong: even --continue stops.
                    if (!o.Continue || end.ExitCode == 4 || end.Declined) { stopCode = end.ExitCode; break; }
                    // The group is rolled back, so a later task on one of its files would edit code the spec assumed changed.
                    var files = new HashSet<string>(group.Select(FullPath), SpecTaskPaths.Comparer);
                    if (todo.Skip(i + 1).FirstOrDefault(t => files.Contains(FullPath(t))) is { } blocked)
                    {
                        owner.Ui.Warn($"그룹 {groupId}이 실패해 그 파일을 대상으로 하는 작업 {blocked.Id}부터는 실행하지 않습니다.");
                        stopCode = end.ExitCode;
                        break;
                    }
                    continue;
                }

                // Only a criterion's first linked task gets RED, so under --resume a criterion whose first linked task is
                // already done has no RED role left (its result shows Skipped). Group tasks get none: the group tests them.
                RunResult? run;
                try
                {
                    run = await ExecuteTaskAsync(tx, task, taskOptions with { Tests = TestFirstLinks.For(_links, task.Id) }, ct);
                }
                catch (OperationCanceledException)
                {
                    // The task rolled itself back; completed tasks keep their marks (or, under review, are rolled back).
                    return Finish(await CancelledAsync(tx));
                }
                if (run is not { } r) return Finish(Unreviewed(tx, 4));

                if (r.ExitCode == 0)
                {
                    if (!await SpecWrittenAsync(_doc.MarkDoneAsync(task.Id, r.RunId, CancellationToken.None),
                            $"작업 {task.Id}은 적용됐지만 체크 표시를 남기지 못했습니다 — --resume 전에 직접 [x]로 표시하세요."))
                        return Finish(Unreviewed(tx, 4));
                    _marked.Add(task.Id);
                    _done[task.Id] = (r.RunId, r.Original?.Path ?? FullPath(task));
                    continue;
                }

                anyFailed = true;
                if (o.Continue && r.ExitCode == 4) { stopCode = 4; break; } // environment errors fail every later task too

                string kind = r.FailureKind ?? $"exit-{r.ExitCode}";
                if (!await SpecWrittenAsync(_doc.MarkFailedAsync(task.Id, r.RunId, kind, CancellationToken.None))) return Finish(Unreviewed(tx, 4));
                _marked.Add(task.Id);
                // The user declined a test that already passes: the spec's criterion is wrong, so even --continue stops.
                if (kind == FailureKinds.TestRedDeclined) { stopCode = 2; break; }
                if (!o.Continue) { stopCode = r.ExitCode; break; }
            }

            if (o.ReviewAtEnd && await ReviewAtEndAsync(tx, ct) is int reviewCode) return Finish(reviewCode);

            if (stopCode is null && _doc.Spec.Tasks.All(t => t.Done))
            {
                if (!await SpecWrittenAsync(_doc.SetStatusAsync(SpecStatus.Done, CancellationToken.None))) return Finish(4);
                Archive();
            }

            return Finish(stopCode ?? (anyFailed ? 1 : 0));
        }

        private async Task<int> DryRunAsync(List<SpecTask> todo, CancellationToken ct)
        {
            if (todo.Count == 0)
            {
                owner.Ui.Warn("실행할 작업이 없습니다.");
                return 0;
            }

            try
            {
                // A group task runs alone in group mode: no build, so nothing to verify the group with.
                var group = todo[0].Group is null ? null : new GroupRun(null);
                var r = await owner.Executor.ExecuteAsync(todo[0], ws, new TaskRunOptions(o.Yes, true, o.MaxRetries, o.Verbose, group), ct);
                return r.ExitCode;
            }
            catch (OperationCanceledException)
            {
                return 2;
            }
        }

        /// <summary>
        /// How a group ended: its exit code, whether the apply ends now with that code, and whether the user declined a test
        /// that already passes (the apply stops even under --continue).
        /// </summary>
        private readonly record struct GroupEnd(int ExitCode, bool Terminal, bool Declined = false);

        /// <summary>A group's test plan and its baseline (design §7.3); null for a group without linked tests.</summary>
        private sealed record GroupTesting(GroupTestPlan Plan, GroupTestBaseline Baseline);

        /// <summary>One group's tasks and what their runs left.</summary>
        private sealed class GroupState(IReadOnlyList<SpecTask> tasks)
        {
            public string Id { get; } = tasks[0].Group!;
            public IReadOnlyList<SpecTask> Tasks { get; } = tasks;

            /// <summary>Task ID → its last run (any outcome).</summary>
            public Dictionary<string, string> LastRun { get; } = new(StringComparer.Ordinal);

            /// <summary>
            /// Task ID → its last successful run, file, target display and the private helpers its runs added. Only a successful
            /// run's display is the member in the file: after a failure the FSM rolled the file back, but the display may still
            /// show the rejected rename.
            /// </summary>
            public Dictionary<string, (string RunId, string Path, string? Display, IReadOnlyList<string> Helpers)> Succeeded { get; } =
                new(StringComparer.Ordinal);
        }

        /// <summary>Runs a group (design §6 "작업 그룹"): scope, baseline build, tasks, verification with re-runs, approval, marks.</summary>
        private async Task<GroupEnd> RunGroupAsync(ApplyTransaction tx, IReadOnlyList<SpecTask> tasks, CancellationToken ct)
        {
            var g = new GroupState(tasks);
            var changed = new List<string>();
            foreach (var task in tasks)
            {
                if (ProjectFileLocator.FindNearestCsproj(owner.Fs, FullPath(task)) is not { } csproj)
                    return await UnverifiableBeforeEditAsync(tx, g, $"그룹 빌드 검증 불가 ({task.Path}): 파일이 속한 프로젝트(.csproj)를 찾을 수 없습니다");
                if (!changed.Contains(csproj, PathComparers.ForOs)) changed.Add(csproj);
            }

            IReadOnlyList<string> scope;
            try { scope = ProjectReferenceGraph.AffectedProjects(owner.Fs, ws.Root, changed); }
            catch (ProjectScopeException ex)
            {
                // A dependent the scope might miss would never be built: the group would pass with the solution broken.
                return await UnverifiableBeforeEditAsync(tx, g, $"그룹 빌드 검증 불가: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return await UnverifiableBeforeEditAsync(tx, g, $"그룹 빌드 검증 불가: 프로젝트 참조를 읽을 수 없습니다 ({ex.Message})");
            }

            GroupBuild baseline;
            try { baseline = await owner.Groups!.BuildAsync(ws, scope, ct); }
            catch (OperationCanceledException) { return new GroupEnd(await CancelledAsync(tx), true); }
            if (!baseline.Verifiable) return await UnverifiableBeforeEditAsync(tx, g, baseline.Reason!);
            if (BaselineHidesDependents(scope, baseline) is { } hidden) return await UnverifiableBeforeEditAsync(tx, g, hidden);

            GroupTesting? testing = null;
            if (Plan(g) is { } plan)
            {
                GroupTestBaseline tests;
                try { tests = await owner.GroupTests!.BaselineAsync(ws, scope, plan, ct); }
                catch (OperationCanceledException) { return new GroupEnd(await CancelledAsync(tx), true); }
                if (tests.Unverifiable is { } reason)
                    return await UnverifiableBeforeEditAsync(tx, g, $"그룹 {g.Id}: {reason}", GroupFailureKinds.TestUnverifiable);
                if (await JudgeGroupRedAsync(tx, g, plan, tests, ct) is { } declined) return declined;
                testing = new GroupTesting(plan, tests);
            }

            tx.BeginGroup(g.Id);
            try
            {
                return await RunGroupTasksAsync(tx, g, scope, baseline, testing, ct);
            }
            catch (OperationCanceledException)
            {
                // Never leave a half-applied group: undo it, then cancel as a single task would.
                var undone = await UndoGroupAsync(tx, g, null, 2);
                if (undone.Terminal) return undone;
                int code = await CancelledAsync(tx);
                return new GroupEnd(undone.ExitCode == 4 ? 4 : code, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unexpected failure: the group's edits were never verified or approved, so they must not stay. A failure
                // of the undo itself must not hide the original exception.
                try { await UndoGroupAsync(tx, g, null, 4); }
                catch (Exception undo)
                {
                    try { owner.Ui.Warn($"그룹 {g.Id}을 되돌리는 중 오류가 났습니다 ({undo.GetType().Name}: {undo.Message}). 그룹 파일을 직접 확인하세요."); }
                    catch (Exception warn) when (warn is not OperationCanceledException) { /* the original exception is what matters */ }
                }
                throw;
            }
        }

        /// <summary>
        /// The reason the group cannot be verified when a scope project another scope project references already fails to
        /// compile: the build of the referencing project stops there, so its own breakage would never show. A compiler error
        /// without a file cannot be placed, so it counts against any scope with such a reference. Null when nothing is hidden.
        /// </summary>
        private string? BaselineHidesDependents(IReadOnlyList<string> scope, GroupBuild baseline)
        {
            if (baseline.Errors.Count == 0) return null;
            var inScope = new HashSet<string>(scope, PathComparers.ForOs);
            var referenced = new HashSet<string>(PathComparers.ForOs);
            foreach (var project in scope)
                foreach (var dependency in ProjectReferenceGraph.References(owner.Fs, project))
                    if (inScope.Contains(dependency) && !PathComparers.ForOs.Equals(dependency, project)) referenced.Add(dependency);
            if (referenced.Count == 0) return null;

            foreach (var error in baseline.Errors)
            {
                if (string.IsNullOrEmpty(error.File))
                    return $"그룹 빌드 검증 불가: 원본 상태에 위치를 알 수 없는 컴파일 오류({error.Code})가 있어 참조하는 프로젝트를 검증할 수 있는지 알 수 없습니다";
                var project = ProjectFileLocator.FindNearestCsproj(owner.Fs, owner.Fs.Path.Combine(ws.Root, error.File.Replace('\\', '/')));
                if (project is null || !referenced.Contains(project)) continue;
                var relative = owner.Fs.Path.GetRelativePath(ws.Root, project).Replace('\\', '/');
                return $"그룹 빌드 검증 불가 ({relative}): 참조 프로젝트 {relative}에 이미 컴파일 오류가 있어 이를 참조하는 프로젝트를 검증할 수 없습니다 ({error.Code})";
            }
            return null;
        }

        /// <summary>
        /// The group's test plan from its tasks' criterion tests (design §7.3): RED for a test whose criterion's first linked task
        /// is in the group, GREEN for one whose last linked task is; null when no criterion test links a group task.
        /// </summary>
        private GroupTestPlan? Plan(GroupState g)
        {
            var tests = new List<string>();
            var red = new List<string>();
            var green = new List<string>();
            var breaks = new List<string>();
            static void Add(List<string> list, string name)
            {
                if (!list.Contains(name, StringComparer.Ordinal)) list.Add(name);
            }
            foreach (var task in g.Tasks)
            {
                if (TestFirstLinks.For(_links, task.Id) is not { } checks) continue;
                foreach (var (name, check) in checks.Checks)
                {
                    Add(tests, name);
                    if (check.HasFlag(TestCheck.Red)) Add(red, name);
                    if (check.HasFlag(TestCheck.Green)) Add(green, name);
                }
                foreach (var b in checks.Breaks) Add(breaks, b);
            }
            if (tests.Count == 0) return null;
            var prefix = owner.Fs.Path.Combine(ws.PellyDir, "runs", ApplyId!, "group-tests", g.Id);
            return new GroupTestPlan(tests, red, green, breaks) { ReportPrefix = prefix };
        }

        /// <summary>
        /// RED of the group's baseline: records each criterion's state, shows the notices and, for a test with the RED role that
        /// already passes, warns and (interactive, without --yes or --review-at-end) asks. Null to go on; otherwise the group's
        /// end after the user declined: no edit, every task marked <c>TestRedDeclined</c>, exit 2.
        /// </summary>
        private async Task<GroupEnd?> JudgeGroupRedAsync(ApplyTransaction tx, GroupState g, GroupTestPlan plan, GroupTestBaseline tests,
            CancellationToken ct)
        {
            var ids = g.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var link in _links)
                if (link.LinkedTaskIds.Count > 0 && ids.Contains(link.LinkedTaskIds[0]) && tests.Red.TryGetValue(link.Test, out var state))
                    _groupRed[link.AcId] = state;
            foreach (var notice in tests.Notices) owner.Ui.Warn(notice);

            var passed = plan.RedNames.Where(n => tests.Red.GetValueOrDefault(n) == RedState.AlreadyPassed).ToList();
            if (passed.Count == 0) return null;
            owner.Ui.Warn($"이 테스트는 변경을 검증하지 못합니다 (원본 상태에서 이미 통과): {string.Join(", ", passed)}");
            if (!owner.Ui.IsInteractive || o.Yes || o.ReviewAtEnd) return null;

            bool go;
            try { go = await owner.Ui.ConfirmAsync("그래도 계속할까요?", false, ct); }
            catch (OperationCanceledException) { return new GroupEnd(await CancelledAsync(tx), true); }
            if (go) return null;

            foreach (var t in g.Tasks) _results.Add(new ApplyTaskResult(t.Id, null, 2, FailureKinds.TestRedDeclined, 0, g.Id));
            var marks = g.Tasks.Select(t => new SpecMark(t.Id, ApplyId!, FailureKinds.TestRedDeclined)).ToList();
            if (!await SpecWrittenAsync(_doc.MarkAsync(marks, CancellationToken.None))) return new GroupEnd(Unreviewed(tx, 4), true);
            _marked.AddRange(g.Tasks.Select(t => t.Id));
            return new GroupEnd(2, false, Declined: true);
        }

        private async Task<GroupEnd> RunGroupTasksAsync(ApplyTransaction tx, GroupState g, IReadOnlyList<string> scope, GroupBuild baseline,
            GroupTesting? testing, CancellationToken ct)
        {
            foreach (var task in g.Tasks)
            {
                if (await RunGroupTaskAsync(tx, g, task, new GroupRun(null), null, ct) is not { } r) return await GroupAbortedAsync(tx, g);
                if (r.ExitCode != 0) return await UndoGroupAsync(tx, g, r.FailureKind ?? $"exit-{r.ExitCode}", r.ExitCode);
            }

            int max = o.MaxRetries ?? owner.GroupRetryMax;
            for (int reruns = 0; ; reruns++)
            {
                var build = await owner.Groups!.BuildAsync(ws, scope, ct);
                if (!build.Verifiable)
                {
                    // The edits cannot be judged; keeping them would ship unverified signature changes.
                    owner.Ui.Warn(build.Reason!);
                    return await UndoGroupAsync(tx, g, GroupVerifier.UnverifiableKind, 4);
                }

                var newErrors = GroupVerifier.NewErrors(baseline, build);
                if (newErrors.Count == 0)
                {
                    if (testing is null) break;
                    var verdict = await owner.GroupTests!.VerifyAsync(ws, testing.Baseline, testing.Plan, ct);
                    if (verdict.Unverifiable is { } reason)
                    {
                        owner.Ui.Warn($"그룹 {g.Id}: {reason}");
                        return await UndoGroupAsync(tx, g, GroupFailureKinds.TestUnverifiable, 4);
                    }
                    RecordBreaks(g, verdict);
                    // A compile-RED project with no GREEN test in this group is fixed by a later task outside it, whose own
                    // baseline would then not build: no re-run here can help, and the spec has to group that task too.
                    if (verdict.Unbuilt.FirstOrDefault(u => !u.Value.Any(n => testing.Plan.GreenNames.Contains(n, StringComparer.Ordinal)))
                        is { Key: { } unbuiltProject })
                    {
                        owner.Ui.Warn($"그룹 {g.Id}: 테스트 프로젝트가 그룹 뒤에도 빌드되지 않습니다 — 수용 기준의 마지막 연결 작업까지 같은 그룹에 넣으세요: {unbuiltProject}");
                        return await UndoGroupAsync(tx, g, GroupFailureKinds.TestUnverifiable, 4);
                    }
                    if (verdict.Green.Count == 0 && verdict.TestBuildErrors.Count == 0)
                    {
                        if (verdict.Regressions.Count == 0) break;
                        // A newly failing test belongs to no task: the group cannot be fixed by a re-run (design §7.3).
                        owner.Ui.Warn($"그룹 {g.Id}: 테스트 회귀: {string.Join(", ", verdict.Regressions.Select(r => r.FullName).Distinct())}");
                        return await UndoGroupAsync(tx, g, GroupFailureKinds.TestRegression, 1);
                    }
                    var failing = string.Join(", ", verdict.Green.Select(f => f.FullName).Concat(verdict.Unbuilt.Values.SelectMany(v => v)).Distinct());
                    if (reruns >= max)
                    {
                        owner.Ui.Warn($"그룹 {g.Id}: 재실행 {max}번 뒤에도 테스트가 통과하지 않습니다: {failing}");
                        return await UndoGroupAsync(tx, g, GroupFailureKinds.TestGreenFailed, 1);
                    }
                    if (await RerunForTestsAsync(tx, g, testing, verdict, failing, ct) is { } failed) return failed;
                    continue;
                }

                var members = g.Tasks.Select(t => new GroupMember(t.Id, FullPath(t), g.Succeeded[t.Id].Display ?? "", g.Succeeded[t.Id].Helpers)).ToList();
                var attribution = GroupErrorAttribution.Attribute(owner.Fs, ws, newErrors, members, LanguageVersion.Latest);
                if (attribution.Unattributed.Count > 0)
                {
                    // A re-run cannot fix a member no task may touch: the spec is missing a task.
                    owner.Ui.Warn($"그룹 {g.Id}: 명세에 빠진 영향 대상: {string.Join(", ", attribution.Unattributed.Select(u => u.Location).Distinct())}");
                    return await UndoGroupAsync(tx, g, GroupFailureKinds.MissingTarget, 1);
                }
                if (reruns >= max)
                {
                    owner.Ui.Warn($"그룹 {g.Id}: 재실행 {max}번 뒤에도 그룹 빌드에 새 오류가 있습니다: {string.Join(", ", newErrors.Select(e => e.Code).Distinct())}");
                    return await UndoGroupAsync(tx, g, GroupFailureKinds.BuildFailed, 1);
                }

                // Every feedback quotes the files as this build saw them: a re-run rewrites its file, and a later task on
                // the same file would otherwise quote shifted lines under the build's line numbers.
                var attributed = g.Tasks.Where(t => attribution.ByTask.ContainsKey(t.Id)).ToList();
                var feedbacks = BuildFeedbacks(attributed, attribution);
                foreach (var task in attributed)
                {
                    // The member may have been renamed: name it as the last successful run left it, never from the spec.
                    var display = g.Succeeded[task.Id].Display;
                    var feedback = feedbacks[task.Id];
                    var member = display is null ? null : MemberLocator.NameOf(display);
                    // The helpers its runs added may hold the error: the re-run may change them.
                    var rerun = new GroupRun(feedback, g.Succeeded[task.Id].Helpers);
                    if (await RunGroupTaskAsync(tx, g, task, rerun, member, ct) is not { } r) return await GroupAbortedAsync(tx, g);
                    if (r.ExitCode != 0) return await UndoGroupAsync(tx, g, r.FailureKind ?? $"exit-{r.ExitCode}", r.ExitCode);
                }
            }

            // --review-at-end leaves the group to the review of the whole apply.
            if (!o.Yes && !o.ReviewAtEnd)
            {
                foreach (var (file, before, after) in tx.GroupChanges()) owner.Ui.ShowDiff(file, before, after);
                if (!await owner.Ui.ConfirmAsync("이 그룹을 적용할까요?", false, ct))
                    return await UndoGroupAsync(tx, g, GroupFailureKinds.Rejected, 2);
            }

            tx.EndGroup();
            var marks = g.Tasks.Select(t => new SpecMark(t.Id, g.Succeeded[t.Id].RunId)).ToList();
            if (!await SpecWrittenAsync(_doc.MarkAsync(marks, CancellationToken.None),
                    $"그룹 {g.Id}은 적용됐지만 체크 표시를 남기지 못했습니다 — --resume 전에 직접 [x]로 표시하세요."))
                return new GroupEnd(Unreviewed(tx, 4), true);
            foreach (var task in g.Tasks)
            {
                _marked.Add(task.Id);
                _done[task.Id] = (g.Succeeded[task.Id].RunId, g.Succeeded[task.Id].Path);
            }
            return new GroupEnd(0, false);
        }

        /// <summary>Records the expected breaks the verdict left out for every criterion whose last linked task is in the group.</summary>
        private void RecordBreaks(GroupState g, GroupTestVerdict verdict)
        {
            var ids = g.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var link in _links)
                if (link.LinkedTaskIds.Count > 0 && ids.Contains(link.LinkedTaskIds[^1])) _groupBreaks[link.AcId] = verdict.BreaksExcluded;
        }

        /// <summary>
        /// GREEN failed (design §7.3): each failing test's feedback goes to its criterion's last linked task in the group (test
        /// feedback, or the test project's build errors when it still does not build), and only those tasks run again. Null to
        /// build again; otherwise the group's end when a re-run failed or its file could not be backed up, or at once with
        /// <c>GroupTestGreenFailed</c> when no failing test belongs to a group task (no re-run could change the verdict).
        /// </summary>
        private async Task<GroupEnd?> RerunForTestsAsync(ApplyTransaction tx, GroupState g, GroupTesting testing, GroupTestVerdict verdict,
            string failing, CancellationToken ct)
        {
            var ids = g.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            // Each criterion naming the test (two may spell it differently) owns it through its last linked task in the group.
            IEnumerable<string> Owners(string name) => _links.Where(l => l.Test == name)
                .Select(l => l.LinkedTaskIds.LastOrDefault(ids.Contains)).OfType<string>();
            var names = testing.Baseline.Tests.GroupBy(t => t.FullName, StringComparer.Ordinal)
                .ToDictionary(t => t.Key, t => t.Select(x => x.Name).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

            var green = new Dictionary<string, List<GreenFailure>>(StringComparer.Ordinal);
            foreach (var failure in verdict.Green)
                foreach (var id in names.GetValueOrDefault(failure.FullName, []).SelectMany(Owners).Distinct(StringComparer.Ordinal))
                    (green.TryGetValue(id, out var list) ? list : green[id] = []).Add(failure);
            var unbuilt = verdict.Unbuilt.Values.SelectMany(v => v).SelectMany(Owners).ToHashSet(StringComparer.Ordinal);

            // Built before any re-run against the files as they are now, like the build re-runs' feedback.
            var feedbacks = new Dictionary<string, Feedback>(StringComparer.Ordinal);
            foreach (var task in g.Tasks)
            {
                var full = FullPath(task);
                var relative = owner.Fs.Path.GetRelativePath(ws.Root, full).Replace('\\', '/');
                if (unbuilt.Contains(task.Id))
                    feedbacks[task.Id] = owner.GroupFeedback.ForBuild(verdict.TestBuildErrors, relative, ReadText(full));
                else if (green.TryGetValue(task.Id, out var failures))
                    feedbacks[task.Id] = owner.GroupFeedback.ForGreen(failures, verdict.Regressions, relative);
            }

            if (feedbacks.Count == 0)
            {
                owner.Ui.Warn($"그룹 {g.Id}: 그룹 작업으로 고칠 수 없는 테스트가 통과하지 않습니다: {failing}");
                return await UndoGroupAsync(tx, g, GroupFailureKinds.TestGreenFailed, 1);
            }

            foreach (var task in g.Tasks.Where(t => feedbacks.ContainsKey(t.Id)))
            {
                var display = g.Succeeded[task.Id].Display;
                var member = display is null ? null : MemberLocator.NameOf(display);
                var rerun = new GroupRun(feedbacks[task.Id], g.Succeeded[task.Id].Helpers);
                if (await RunGroupTaskAsync(tx, g, task, rerun, member, ct) is not { } r) return await GroupAbortedAsync(tx, g);
                if (r.ExitCode != 0) return await UndoGroupAsync(tx, g, r.FailureKind ?? $"exit-{r.ExitCode}", r.ExitCode);
            }
            return null;
        }

        private string ReadText(string full)
        {
            try { return owner.Guard.Read(full).Text; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return ""; // the errors still list; the re-run reports the unreadable file itself
            }
        }

        /// <summary>
        /// Runs one group task (always without review: the group is approved as a whole). Null when its file could not be
        /// backed up. Throws <see cref="OperationCanceledException"/> when the apply was cancelled.
        /// </summary>
        private async Task<RunResult?> RunGroupTaskAsync(ApplyTransaction tx, GroupState g, SpecTask task, GroupRun run, string? member,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (await ExecuteTaskAsync(tx, task, new TaskRunOptions(true, false, o.MaxRetries, o.Verbose, run, member), ct) is not { } r)
                return null;

            g.LastRun[task.Id] = r.RunId;
            if (r.ExitCode == 0) g.Succeeded[task.Id] = (r.RunId, r.Original?.Path ?? FullPath(task), r.TargetDisplay, r.AddedHelpers ?? run.EditableHelpers ?? []);
            return r;
        }

        /// <summary>
        /// Runs one task, single or in a group: backs its file up, runs it, adds its result to the summary and records what
        /// it left in the transaction. Null when the file could not be backed up (the task did not run). Throws
        /// <see cref="OperationCanceledException"/> when the apply was cancelled: the executor threw it, or the run ended
        /// cancelled under the apply's token.
        /// </summary>
        private async Task<RunResult?> ExecuteTaskAsync(ApplyTransaction tx, SpecTask task, TaskRunOptions options, CancellationToken ct)
        {
            if (!TrackBeforeRun(tx, task)) return null;

            long started = owner.Time.GetTimestamp();
            RunResult r;
            try
            {
                r = await owner.Executor.ExecuteAsync(task, ws, options, ct);
            }
            catch (OperationCanceledException)
            {
                _results.Add(new ApplyTaskResult(task.Id, null, 2, null, Seconds(started), task.Group));
                throw;
            }

            _results.Add(new ApplyTaskResult(task.Id, r.RunId, r.ExitCode, r.FailureKind, Seconds(started), task.Group));
            _firstRuns.TryAdd(task.Id, r);
            _lastRuns[task.Id] = r;
            _ends[task.Id] = r.ExitCode;
            // A null Original means the run failed before reading the file, so it wrote nothing.
            if (r.Original is not null) tx.Record(r.Original, r.Final);
            if (r.ExitCode == 2 && ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return r;
        }

        /// <summary>
        /// Each attributed task's build errors as the FSM's own build feedback, all built before any re-run against the
        /// files as they are now (one read per file).
        /// </summary>
        private Dictionary<string, Feedback> BuildFeedbacks(IReadOnlyList<SpecTask> tasks, Attribution attribution)
        {
            var texts = new Dictionary<string, string>(SpecTaskPaths.Comparer);
            var feedbacks = new Dictionary<string, Feedback>(StringComparer.Ordinal);
            foreach (var task in tasks)
            {
                string full = FullPath(task);
                if (!texts.TryGetValue(full, out var text))
                {
                    try { text = owner.Guard.Read(full).Text; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        text = ""; // the errors still list; the re-run reports the unreadable file itself
                    }
                    texts[full] = text;
                }
                var relative = owner.Fs.Path.GetRelativePath(ws.Root, full).Replace('\\', '/');
                feedbacks[task.Id] = owner.GroupFeedback.ForBuild(attribution.ByTask[task.Id], relative, text);
            }
            return feedbacks;
        }

        /// <summary>
        /// Nothing changed yet: every group task is marked <paramref name="kind"/> (default <c>GroupUnverifiable</c>) under the
        /// apply ID, exit 4.
        /// </summary>
        private async Task<GroupEnd> UnverifiableBeforeEditAsync(ApplyTransaction tx, GroupState g, string reason,
            string kind = GroupVerifier.UnverifiableKind)
        {
            owner.Ui.Warn(reason);
            // No task ran, so the report would not see the group otherwise.
            foreach (var t in g.Tasks) _results.Add(new ApplyTaskResult(t.Id, null, 4, kind, 0, g.Id));
            var marks = g.Tasks.Select(t => new SpecMark(t.Id, ApplyId!, kind)).ToList();
            if (!await SpecWrittenAsync(_doc.MarkAsync(marks, CancellationToken.None))) return new GroupEnd(Unreviewed(tx, 4), true);
            _marked.AddRange(g.Tasks.Select(t => t.Id));
            return new GroupEnd(4, false);
        }

        /// <summary>
        /// A group that fails as a whole ends every task of it with the group's kind in the summary, so the report can tell the
        /// group's outcome from its tasks' own exit codes. A task that failed by itself keeps its own kind.
        /// </summary>
        private void StampGroupFailure(GroupState g, string kind)
        {
            var ids = g.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            for (int i = 0; i < _results.Count; i++)
                if (ids.Contains(_results[i].TaskId) && _results[i].FailureKind is null) _results[i] = _results[i] with { FailureKind = kind };
        }

        /// <summary>A group task's file could not be backed up: undo the group and stop, as a single task would.</summary>
        private async Task<GroupEnd> GroupAbortedAsync(ApplyTransaction tx, GroupState g)
        {
            var undone = await UndoGroupAsync(tx, g, null, 4);
            return undone.Terminal ? undone : new GroupEnd(Unreviewed(tx, 4), true);
        }

        /// <summary>
        /// Rolls the group back and closes it. With <paramref name="failureKind"/> every group task gets the failed comment
        /// (its last run, or the apply ID when it never ran); without, the tasks stay unmarked. Tasks whose file could not
        /// be restored keep their edits, so they get [x] instead, and the result is 4; otherwise <paramref name="exitCode"/>.
        /// </summary>
        private async Task<GroupEnd> UndoGroupAsync(ApplyTransaction tx, GroupState g, string? failureKind, int exitCode)
        {
            var notRestored = await tx.RollbackGroupAsync(CancellationToken.None); // also closes the group
            if (GroupFailureKinds.IsGroupKind(failureKind)) StampGroupFailure(g, failureKind!);

            var unrestored = new HashSet<string>(notRestored, SpecTaskPaths.Comparer);
            var kept = g.Tasks.Where(t => g.Succeeded.TryGetValue(t.Id, out var s) && unrestored.Contains(s.Path)).Select(t => t.Id).ToList();
            var marks = new List<SpecMark>();
            foreach (var task in g.Tasks)
            {
                if (kept.Contains(task.Id)) marks.Add(new SpecMark(task.Id, g.Succeeded[task.Id].RunId));
                else if (failureKind is not null) marks.Add(new SpecMark(task.Id, g.LastRun.GetValueOrDefault(task.Id) ?? ApplyId!, failureKind));
            }
            // Before the marks: a failed spec write must not hide the files the group left half-applied.
            if (notRestored.Count > 0)
            {
                owner.Ui.Warn($"그룹 {g.Id}을 되돌리는 중 pelly 밖에서 바뀌어 되돌리지 못한 파일이 있습니다:\n"
                    + string.Join("\n", notRestored.Select(p => "  " + p))
                    + (kept.Count > 0 ? $"\n이 파일의 작업은 체크 표시를 유지합니다: {string.Join(", ", kept)}" : "")
                    + "\n그룹 일부가 적용된 채 남아 빌드가 깨져 있을 수 있습니다.");
            }

            if (marks.Count > 0)
            {
                var consequence = kept.Count > 0
                    ? $"작업 {string.Join(", ", kept)}의 체크 표시를 남기지 못했습니다 — --resume 전에 직접 [x]로 표시하세요."
                    : "이미 적용된 작업은 그대로 둡니다.";
                if (!await SpecWrittenAsync(_doc.MarkAsync(marks, CancellationToken.None), consequence)) return new GroupEnd(Unreviewed(tx, 4), true);
                _marked.AddRange(marks.Select(m => m.TaskId));
                foreach (var id in kept) _done[id] = (g.Succeeded[id].RunId, g.Succeeded[id].Path);
            }

            return new GroupEnd(notRestored.Count == 0 ? exitCode : 4, false);
        }

        /// <summary>
        /// Backs the task's file up before the task runs: <see cref="FileGuard.Backup"/> checks the disk still holds the
        /// snapshot, which is no longer true once the task has written. The path is built as the FSM builds it, so the
        /// later <c>Record(result.Original, …)</c> finds the same entry and compares what the task read with this read.
        /// </summary>
        private bool TrackBeforeRun(ApplyTransaction tx, SpecTask task)
        {
            string full = FullPath(task);
            FileSnapshot original;
            try { original = owner.Guard.Read(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return true; // the task reports the unreadable file itself
            }

            try
            {
                tx.Track(original);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                owner.Ui.Warn($"작업 {task.Id}: 원본을 백업할 수 없어 멈춥니다: {ex.Message}");
                return false;
            }
        }

        // The same path FsmTaskExecutor hands the FSM.
        private string FullPath(SpecTask task) => SpecTaskPaths.FullPath(owner.Fs.Path, ws.Root, task.Path);

        /// <summary>
        /// Cancellation: without --review-at-end every applied task was approved, so it stays. Under --review-at-end
        /// nothing was reviewed, so the whole apply is rolled back as if the review were rejected.
        /// </summary>
        private async Task<int> CancelledAsync(ApplyTransaction tx) =>
            o.ReviewAtEnd ? await RollBackApplyAsync(tx, "취소를 반영하지 못했습니다. 파일과 체크 표시를 그대로 둡니다.") : 2;

        /// <summary>
        /// A stop that cannot roll back (the spec file cannot record it): under --review-at-end the applied edits were
        /// never reviewed, so they are named. Returns <paramref name="exitCode"/>.
        /// </summary>
        private int Unreviewed(ApplyTransaction tx, int exitCode)
        {
            if (!o.ReviewAtEnd) return exitCode;
            var files = tx.Changes();
            if (files.Count > 0)
                owner.Ui.Warn("검토하지 않은 변경이 파일에 남아 있습니다. 직접 확인하세요:\n" + string.Join("\n", files.Select(f => "  " + f.Path)));
            return exitCode;
        }

        /// <summary>Null when the changes are kept; otherwise the exit code after rolling the apply back.</summary>
        private async Task<int?> ReviewAtEndAsync(ApplyTransaction tx, CancellationToken ct)
        {
            var changes = tx.Changes();
            if (changes.Count == 0) return null;
            foreach (var (file, before, after) in changes) owner.Ui.ShowDiff(file, before, after);
            // The FSM only recorded these under --review-at-end (it never asks then): the reviewer decides with them in view.
            var passed = _links.Where(l => RedOf(l) == RedState.AlreadyPassed).Select(l => l.AcId).ToList();
            if (passed.Count > 0) owner.Ui.Warn($"이미 통과한 테스트로 판정된 수용 기준: {string.Join(", ", passed)}");

            bool approved;
            try { approved = await owner.Ui.ConfirmAsync("모두 적용할까요?", false, ct); }
            catch (OperationCanceledException) { approved = false; } // Ctrl+C at the question means "do not apply"
            if (approved) return null;
            return await RollBackApplyAsync(tx, "끝 승인 거부를 반영하지 못했습니다. 파일과 체크 표시를 그대로 둡니다.");
        }

        /// <summary>Undoes this apply (marks first, then files); 2 when everything was restored, otherwise 4.</summary>
        private async Task<int> RollBackApplyAsync(ApplyTransaction tx, string notApplied)
        {
            // Spec first: if the marks cannot be cleared, the files must stay too, or a rolled-back task would keep its [x].
            if (_marked.Count > 0 && !await SpecWrittenAsync(_doc.ClearMarksAsync(_marked, CancellationToken.None), notApplied)) return 4;
            // Tasks done in an earlier session (--resume) keep their marks, so the spec stays in progress.
            var status = _doc.Spec.Tasks.Any(t => t.Done) ? SpecStatus.InProgress : SpecStatus.Draft;
            if (!await SpecWrittenAsync(_doc.SetStatusAsync(status, CancellationToken.None), notApplied)) return 4;

            var notRestored = new HashSet<string>(await tx.RollbackAsync(CancellationToken.None), SpecTaskPaths.Comparer);
            if (notRestored.Count == 0)
            {
                _rolledBack = true; // only a full rollback undoes every task; a partial one leaves edits and marks in place
                return 2;
            }

            // A file changed outside pelly keeps the tasks' edits, so those tasks keep their [x].
            var kept = _done.Where(d => notRestored.Contains(d.Value.Path)).Select(d => d.Key).ToList();
            foreach (var id in kept)
                if (!await SpecWrittenAsync(_doc.MarkDoneAsync(id, _done[id].RunId, CancellationToken.None),
                        $"작업 {id}의 체크 표시를 되살리지 못했습니다 — --resume 전에 직접 [x]로 표시하세요."))
                    return 4;
            if (kept.Count > 0 && status != SpecStatus.InProgress)
                await SpecWrittenAsync(_doc.SetStatusAsync(SpecStatus.InProgress, CancellationToken.None));

            owner.Ui.Warn("pelly 밖에서 바뀌어 되돌리지 못한 파일이 있습니다:\n" + string.Join("\n", notRestored.Select(p => "  " + p))
                + (kept.Count > 0 ? $"\n이 파일의 작업은 체크 표시를 유지합니다: {string.Join(", ", kept)}" : ""));
            return 4;
        }

        private void Archive()
        {
            var date = DateOnly.FromDateTime(owner.Time.GetLocalNow().DateTime);
            try
            {
                SpecArchiver.Archive(owner.Fs, specPath, date, SpecId);
                _archived = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                owner.Ui.Warn($"명세를 보관 폴더로 옮기지 못했습니다: {ex.Message}");
            }
        }

        /// <param name="consequence">What the failed write leaves behind; defaults to "applied tasks stay as they are".</param>
        private async Task<bool> SpecWrittenAsync(Task<FileWriteResult> write, string consequence = "이미 적용된 작업은 그대로 둡니다.")
        {
            var r = await write;
            if (r.Status == FileWriteStatus.Written) return true;
            owner.Ui.Warn((r.Status == FileWriteStatus.ExternallyChanged
                ? "명세 파일이 실행 중에 바뀌었습니다. "
                : $"명세 파일을 쓸 수 없습니다({r.Detail}). ") + consequence);
            return false;
        }

        private string SpecId => _doc.Spec.Id ?? owner.Fs.Path.GetFileNameWithoutExtension(specPath);

        private double Seconds(long started) => owner.Time.GetElapsedTime(started).TotalSeconds;

        /// <summary>RED of a criterion: its first linked task's verdict for the test; not judged when absent (or never run).</summary>
        private RedState RedOf(AcLink link)
        {
            if (link.LinkedTaskIds.Count == 0) return RedState.NotChecked;
            var first = link.LinkedTaskIds[0];
            if (_doneAtStart.Contains(first)) return RedState.Skipped;
            if (_groupRed.TryGetValue(link.AcId, out var group)) return group;
            return _firstRuns.TryGetValue(first, out var r) && r.TestFirst?.Red.TryGetValue(link.Test, out var state) == true
                ? state
                : RedState.NotChecked;
        }

        /// <summary>
        /// A criterion's result. Its last linked task decides: exit 0 and kept → met; exit 2, or not run in this apply (stopped
        /// earlier, done in an earlier session, review rejected, cancelled) → not run; otherwise not met. A group task ends with its
        /// group, and its expected breaks are the group's last test verdict's.
        /// </summary>
        private AcResult CriterionResult(AcLink link)
        {
            var red = RedOf(link);
            if (link.LinkedTaskIds.Count == 0) return new AcResult(link.AcId, red, AcOutcome.Unverified, null, 0);

            var last = link.LinkedTaskIds[^1];
            var run = _lastRuns.GetValueOrDefault(last);
            var runId = run?.RunId;
            var breaks = _groupBreaks.TryGetValue(link.AcId, out var excluded) ? excluded : run?.TestFirst?.BreaksExcluded ?? 0;
            AcOutcome outcome;
            if (_rolledBack) outcome = AcOutcome.NotRun;
            else if (_ends.TryGetValue(last, out var end))
                outcome = end == 0 ? AcOutcome.Met : end == 2 || run is null ? AcOutcome.NotRun : AcOutcome.NotMet;
            // Not run in this apply, including a last linked task done in an earlier session (--resume): that apply's
            // apply.json already recorded its verdict, so counting it again here would double-count it.
            else outcome = AcOutcome.NotRun;
            return new AcResult(link.AcId, red, outcome, runId, breaks);
        }

        /// <summary>Writes <c>apply.json</c> (best effort) and returns <paramref name="exitCode"/>.</summary>
        private int Finish(int exitCode)
        {
            try
            {
                var dir = owner.Fs.Path.Combine(ws.PellyDir, "runs", ApplyId!);
                owner.Fs.Directory.CreateDirectory(dir);
                var summary = new ApplySummary(ApplyId!, SpecId, _results, exitCode, warnings, _archived,
                    impact is null ? null : impact.Mode == ImpactMode.Semantic ? "semantic" : "name", impact?.Fallback?.Kind.ToString(),
                    _doc?.Spec.Tasks.Count ?? 0, o.Resume, _links.Count > 0 ? _links.Select(CriterionResult).ToList() : null);
                owner.Fs.File.WriteAllText(owner.Fs.Path.Combine(dir, "apply.json"), JsonSerializer.Serialize(summary, SummaryJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The summary is a record of the apply, not part of it.
            }
            return exitCode;
        }
    }
}
