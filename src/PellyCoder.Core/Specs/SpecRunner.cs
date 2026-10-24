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
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
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
public sealed class SpecRunner(IFileSystem fs, FileGuard guard, ITaskExecutor executor, IUserInteraction ui, TimeProvider time, int sqlCompatLevel,
    Action<Workspace, string>? pruneRuns = null, IGroupVerifier? groups = null, FeedbackBuilder? groupFeedback = null, int? groupRetryMax = null)
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
    private int GroupRetryMax => groupRetryMax ?? new RetryOptions().Max;

    private FeedbackBuilder? _defaultFeedback;
    private FeedbackBuilder GroupFeedback =>
        groupFeedback ?? (_defaultFeedback ??= new FeedbackBuilder(QwenTokenCounter.Shared, new BudgetOptions().Feedback));

    public async Task<int> RunAsync(string specPath, ApplyOptions o, CancellationToken ct)
    {
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

        var warnings = check.Issues.Where(i => i.Severity == SpecIssueSeverity.Warning).ToList();
        if (warnings.Count > 0)
        {
            foreach (var issue in warnings) ui.Warn(Format(issue));
            if (ui.IsInteractive && !o.Yes && !await ui.ConfirmAsync("경고가 있습니다. 계속할까요?", false, ct))
                return 2;
        }

        var ws = check.Workspace;
        var held = WorkspaceLock.TryAcquire(fs, ws);
        if (held is null)
        {
            ui.Warn("다른 pelly 실행이 이 작업 공간을 쓰고 있습니다. 끝난 뒤 다시 실행하세요.");
            return 4;
        }

        var run = new ApplyRun(this, ws, specPath, o, warnings);
        try
        {
            return await run.ExecuteAsync(ct);
        }
        finally
        {
            try
            {
                if (run.ApplyId is not null) pruneRuns?.Invoke(ws, run.ApplyId);
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
    private sealed class ApplyRun(SpecRunner owner, Workspace ws, string specPath, ApplyOptions o, IReadOnlyList<SpecIssue> warnings)
    {
        private readonly List<ApplyTaskResult> _results = [];
        private readonly List<string> _marked = [];
        private readonly Dictionary<string, (string RunId, string Path)> _done = new(StringComparer.Ordinal); // task ID → run and file path
        private SpecDocument _doc = null!;
        private bool _archived;

        public string? ApplyId { get; private set; }

        public async Task<int> ExecuteAsync(CancellationToken ct)
        {
            _doc = SpecDocument.Load(owner.Guard, specPath);
            var tasks = _doc.Spec.Tasks;
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
                    if (end.Terminal) return Finish(end.ExitCode);
                    if (end.ExitCode == 0) continue;

                    anyFailed = true;
                    if (!o.Continue || end.ExitCode == 4) { stopCode = end.ExitCode; break; }
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

                if (!TrackBeforeRun(tx, task)) return Finish(Unreviewed(tx, 4));

                long started = owner.Time.GetTimestamp();
                RunResult r;
                try
                {
                    r = await owner.Executor.ExecuteAsync(task, ws, taskOptions, ct);
                }
                catch (OperationCanceledException)
                {
                    // The task rolled itself back; completed tasks keep their marks (or, under review, are rolled back).
                    _results.Add(new ApplyTaskResult(task.Id, null, 2, null, Seconds(started)));
                    return Finish(await CancelledAsync(tx));
                }

                _results.Add(new ApplyTaskResult(task.Id, r.RunId, r.ExitCode, r.FailureKind, Seconds(started)));
                // A null Original means the run failed before reading the file, so it wrote nothing.
                if (r.Original is not null) tx.Record(r.Original, r.Final);

                if (r.ExitCode == 2 && ct.IsCancellationRequested) return Finish(await CancelledAsync(tx));

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

        /// <summary>How a group ended: its exit code, and whether the apply ends now with that code.</summary>
        private readonly record struct GroupEnd(int ExitCode, bool Terminal);

        /// <summary>One group's tasks and what their runs left.</summary>
        private sealed class GroupState(IReadOnlyList<SpecTask> tasks)
        {
            public string Id { get; } = tasks[0].Group!;
            public IReadOnlyList<SpecTask> Tasks { get; } = tasks;

            /// <summary>Task ID → its last run (any outcome).</summary>
            public Dictionary<string, string> LastRun { get; } = new(StringComparer.Ordinal);

            /// <summary>
            /// Task ID → its last successful run, file and target display. Only a successful run's display is the member in
            /// the file: after a failure the FSM rolled the file back, but the display may still show the rejected rename.
            /// </summary>
            public Dictionary<string, (string RunId, string Path, string? Display)> Succeeded { get; } = new(StringComparer.Ordinal);
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

            tx.BeginGroup(g.Id);
            try
            {
                return await RunGroupTasksAsync(tx, g, scope, baseline, ct);
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
                // Unexpected failure: the group's edits were never verified or approved, so they must not stay.
                await UndoGroupAsync(tx, g, null, 4);
                throw;
            }
        }

        private async Task<GroupEnd> RunGroupTasksAsync(ApplyTransaction tx, GroupState g, IReadOnlyList<string> scope, GroupBuild baseline,
            CancellationToken ct)
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
                if (newErrors.Count == 0) break;

                var members = g.Tasks.Select(t => new GroupMember(t.Id, FullPath(t), g.Succeeded[t.Id].Display ?? "")).ToList();
                var attribution = GroupErrorAttribution.Attribute(owner.Fs, ws, newErrors, members, LanguageVersion.Latest);
                if (attribution.Unattributed.Count > 0)
                {
                    // A re-run cannot fix a member no task may touch: the spec is missing a task.
                    owner.Ui.Warn($"그룹 {g.Id}: 명세에 빠진 영향 대상: {string.Join(", ", attribution.Unattributed.Select(u => u.Location).Distinct())}");
                    return await UndoGroupAsync(tx, g, "GroupMissingTarget", 1);
                }
                if (reruns >= max)
                {
                    owner.Ui.Warn($"그룹 {g.Id}: 재실행 {max}번 뒤에도 그룹 빌드에 새 오류가 있습니다: {string.Join(", ", newErrors.Select(e => e.Code).Distinct())}");
                    return await UndoGroupAsync(tx, g, "GroupBuildFailed", 1);
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
                    if (await RunGroupTaskAsync(tx, g, task, new GroupRun(feedback), member, ct) is not { } r) return await GroupAbortedAsync(tx, g);
                    if (r.ExitCode != 0) return await UndoGroupAsync(tx, g, r.FailureKind ?? $"exit-{r.ExitCode}", r.ExitCode);
                }
            }

            // --review-at-end leaves the group to the review of the whole apply.
            if (!o.Yes && !o.ReviewAtEnd)
            {
                foreach (var (file, before, after) in tx.GroupChanges()) owner.Ui.ShowDiff(file, before, after);
                if (!await owner.Ui.ConfirmAsync("이 그룹을 적용할까요?", false, ct))
                    return await UndoGroupAsync(tx, g, "GroupRejected", 2);
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

        /// <summary>
        /// Runs one group task (always without review: the group is approved as a whole). Null when its file could not be
        /// backed up. Throws <see cref="OperationCanceledException"/> when the apply was cancelled.
        /// </summary>
        private async Task<RunResult?> RunGroupTaskAsync(ApplyTransaction tx, GroupState g, SpecTask task, GroupRun run, string? member,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!TrackBeforeRun(tx, task)) return null;

            long started = owner.Time.GetTimestamp();
            RunResult r;
            try
            {
                r = await owner.Executor.ExecuteAsync(task, ws, new TaskRunOptions(true, false, o.MaxRetries, o.Verbose, run, member), ct);
            }
            catch (OperationCanceledException)
            {
                _results.Add(new ApplyTaskResult(task.Id, null, 2, null, Seconds(started)));
                throw;
            }

            _results.Add(new ApplyTaskResult(task.Id, r.RunId, r.ExitCode, r.FailureKind, Seconds(started)));
            if (r.Original is not null) tx.Record(r.Original, r.Final);
            g.LastRun[task.Id] = r.RunId;
            if (r.ExitCode == 2 && ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (r.ExitCode == 0) g.Succeeded[task.Id] = (r.RunId, r.Original?.Path ?? FullPath(task), r.TargetDisplay);
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

        /// <summary>Nothing changed yet: every group task is marked <c>GroupUnverifiable</c> under the apply ID, exit 4.</summary>
        private async Task<GroupEnd> UnverifiableBeforeEditAsync(ApplyTransaction tx, GroupState g, string reason)
        {
            owner.Ui.Warn(reason);
            var marks = g.Tasks.Select(t => new SpecMark(t.Id, ApplyId!, GroupVerifier.UnverifiableKind)).ToList();
            if (!await SpecWrittenAsync(_doc.MarkAsync(marks, CancellationToken.None))) return new GroupEnd(Unreviewed(tx, 4), true);
            _marked.AddRange(g.Tasks.Select(t => t.Id));
            return new GroupEnd(4, false);
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
            var notRestored = await tx.RollbackGroupAsync(CancellationToken.None);
            tx.EndGroup();

            var unrestored = new HashSet<string>(notRestored, SpecTaskPaths.Comparer);
            var kept = g.Tasks.Where(t => g.Succeeded.TryGetValue(t.Id, out var s) && unrestored.Contains(s.Path)).Select(t => t.Id).ToList();
            var marks = new List<SpecMark>();
            foreach (var task in g.Tasks)
            {
                if (kept.Contains(task.Id)) marks.Add(new SpecMark(task.Id, g.Succeeded[task.Id].RunId));
                else if (failureKind is not null) marks.Add(new SpecMark(task.Id, g.LastRun.GetValueOrDefault(task.Id) ?? ApplyId!, failureKind));
            }
            if (marks.Count > 0)
            {
                if (!await SpecWrittenAsync(_doc.MarkAsync(marks, CancellationToken.None))) return new GroupEnd(Unreviewed(tx, 4), true);
                _marked.AddRange(marks.Select(m => m.TaskId));
                foreach (var id in kept) _done[id] = (g.Succeeded[id].RunId, g.Succeeded[id].Path);
            }

            if (notRestored.Count == 0) return new GroupEnd(exitCode, false);
            owner.Ui.Warn($"그룹 {g.Id}을 되돌리는 중 pelly 밖에서 바뀌어 되돌리지 못한 파일이 있습니다:\n"
                + string.Join("\n", notRestored.Select(p => "  " + p))
                + (kept.Count > 0 ? $"\n이 파일의 작업은 체크 표시를 유지합니다: {string.Join(", ", kept)}" : "")
                + "\n그룹 일부가 적용된 채 남아 빌드가 깨져 있을 수 있습니다.");
            return new GroupEnd(4, false);
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
            if (notRestored.Count == 0) return 2;

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

        /// <summary>Writes <c>apply.json</c> (best effort) and returns <paramref name="exitCode"/>.</summary>
        private int Finish(int exitCode)
        {
            try
            {
                var dir = owner.Fs.Path.Combine(ws.PellyDir, "runs", ApplyId!);
                owner.Fs.Directory.CreateDirectory(dir);
                var summary = new ApplySummary(ApplyId!, SpecId, _results, exitCode, warnings, _archived);
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
