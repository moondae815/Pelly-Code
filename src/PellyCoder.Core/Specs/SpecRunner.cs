using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>
/// <c>pelly apply</c>: checks a change spec, then runs its tasks top to bottom through <see cref="ITaskExecutor"/> under one
/// workspace lock, marking each line in the spec as it goes (design §6, groups excluded in V2-1a).
/// </summary>
/// <param name="pruneRuns">Run-log retention, called once at the end under the apply lock (task runs inside an apply skip it).</param>
public sealed class SpecRunner(IFileSystem fs, FileGuard guard, ITaskExecutor executor, IUserInteraction ui, TimeProvider time, int sqlCompatLevel,
    Action<Workspace, string>? pruneRuns = null)
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

            ApplyId = "apply-" + RunLogger.NewRunId(owner.Time);
            if (!await SpecWrittenAsync(_doc.SetStatusAsync(SpecStatus.InProgress, CancellationToken.None))) return Finish(4);

            var tx = new ApplyTransaction(owner.Guard, ws, ApplyId);
            var taskOptions = new TaskRunOptions(o.Yes || o.ReviewAtEnd, false, o.MaxRetries, o.Verbose);
            int? stopCode = null;
            bool anyFailed = false;

            foreach (var task in todo)
            {
                if (ct.IsCancellationRequested) return Finish(await CancelledAsync(tx));
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
                var r = await owner.Executor.ExecuteAsync(todo[0], ws, new TaskRunOptions(o.Yes, true, o.MaxRetries, o.Verbose), ct);
                return r.ExitCode;
            }
            catch (OperationCanceledException)
            {
                return 2;
            }
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
