using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Fsm;

/// <summary>
/// Runs the phases through <see cref="Transitions"/> until Done or Failed (design §7.2). On Failed it restores the
/// original bytes from the backup unless the file changed outside pelly; the restore ignores cancellation.
/// A baseline build or test run still running at the end is cancelled and awaited first, so no such process outlives the run.
/// </summary>
public sealed class FsmOrchestrator(
    IEnumerable<IPhase> phases, FileGuard fileGuard, RunLogger logger, IFileSystem fs,
    PellyOptions options, IUserInteraction ui, TimeProvider time)
{
    private static readonly TimeSpan BaselineStopWait = TimeSpan.FromSeconds(10);

    private readonly Dictionary<RunPhase, IPhase> _phases = phases.ToDictionary(p => p.Phase);

    public async Task<int> RunAsync(RunRequest request, CancellationToken ct) =>
        (await RunDetailedAsync(request, ct)).ExitCode;

    /// <summary>Runs the request and reports how it left the target file (see <see cref="RunResult"/>).</summary>
    public async Task<RunResult> RunDetailedAsync(RunRequest request, CancellationToken ct)
    {
        var ctx = new RunContext(RunLogger.NewRunId(time), request, options);
        var messages = new MessageSink(ui, ctx);
        var logStarted = false;
        // Null until the file was read; then the file as this run leaves it (null when unknown after a failed rollback).
        FileSnapshot? final = null;
        try
        {
            while (!Transitions.IsTerminal(ctx.Phase))
            {
                var phase = ctx.Phase;
                BestEffort(ctx, () => ui.ShowStatus(phase, phase == RunPhase.Edit ? ctx.Attempt + 1 : ctx.Attempt, Describe(ctx, phase)));

                var prior = ctx.FailureKind;
                ctx.FailureKind = null;
                var start = time.GetTimestamp();
                var outcome = await ExecuteAsync(ctx, _phases[phase], ct);
                var elapsed = time.GetElapsedTime(start);
                ctx.FailureKind ??= LabelOf(outcome) ?? prior;
                ctx.PhaseSeconds[phase] = ctx.PhaseSeconds.GetValueOrDefault(phase) + elapsed.TotalSeconds;

                // Logging and display are best effort: they must never turn a phase result into Crashed.
                if (!logStarted && ctx.Workspace is not null)
                {
                    BestEffort(ctx, () => logger.Start(ctx));
                    logStarted = true;
                }
                BestEffort(ctx, () => logger.Phase(ctx, outcome, elapsed));

                var next = Transitions.Next(phase, outcome, ctx.Attempt, ctx.MaxRetries);
                ctx.Phase = next.Next;
                if (next.ExitCode is { } code) ctx.ExitCode = code;
                messages.Flush();
            }
        }
        catch (Exception ex)
        {
            // Only reachable through a bug outside the phases (e.g. an undefined transition): fail safe.
            ctx.Messages.Add($"예기치 않은 오류로 중단합니다: {ex.GetType().Name}: {ex.Message}");
            ctx.FailureKind = "Crashed";
            ctx.Phase = RunPhase.Failed;
            ctx.ExitCode = ExitCodes.EnvError;
        }
        finally
        {
            try
            {
                await StopBaselineAsync(ctx);
                if (ctx.Phase == RunPhase.Failed)
                {
                    final = await RollbackAsync(ctx);
                }
                else
                {
                    ctx.FailureKind = null;
                    final = ctx.Current;
                }

                messages.Flush(); // before Complete, so a display failure is still recorded in messages.txt
                if (!logStarted && ctx.Workspace is not null) BestEffort(ctx, () => logger.Start(ctx));
                BestEffort(ctx, () => logger.Complete(ctx));
                if (request.Verbose)
                {
                    var runDir = ctx.Workspace is { } w ? fs.Path.Combine(w.PellyDir, "runs", ctx.RunId) : null;
                    BestEffort(ctx, () => ui.ShowRunSummary(RunLogger.Summarize(ctx), runDir));
                }
                // Retention only under the lock, after this run's summary is written; never touches the current run.
                if (ctx.Lock is not null && ctx.Workspace is { } ws)
                    BestEffort(ctx, () => logger.Prune(ws, options.Log.RetentionDays, ctx.RunId));
                messages.Flush();
            }
            finally
            {
                ctx.Lock?.Dispose();
            }
        }
        return new RunResult(ctx.ExitCode, ctx.RunId, ctx.FilePath.Length == 0 ? null : ctx.FilePath,
            ctx.Original, final, ctx.FailureKind);
    }

    /// <summary>Runs a logging or display call; a failure is noted in the messages and never changes the outcome.</summary>
    private static void BestEffort(RunContext ctx, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Messages.Add($"로그·화면 출력에 실패했습니다: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, IPhase phase, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return await phase.ExecuteAsync(ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Only the run's own token means the user cancelled; any other cancellation (e.g. an HTTP timeout) is a crash.
            ctx.Messages.Add("사용자가 실행을 중단했습니다.");
            return PhaseOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            ctx.Messages.Add($"예기치 않은 오류로 중단합니다: {ex.GetType().Name}: {ex.Message}");
            return PhaseOutcome.Crashed;
        }
    }

    /// <summary>
    /// Cancels the baseline build and tests (killing their process trees) and waits up to <see cref="BaselineStopWait"/> for both.
    /// Runs before rollback and before the lock is released; its own failures are irrelevant at this point.
    /// </summary>
    private async Task StopBaselineAsync(RunContext ctx)
    {
        if (ctx.BaselineCancellation is not { } cts) return;
        try
        {
            try
            {
                cts.Cancel();
            }
            catch (AggregateException)
            {
                // A cancellation callback threw; the build is still being stopped.
            }
            // The baseline tests start only after the baseline build, so waiting for both shares one time limit.
            var running = new List<Task>();
            if (ctx.Baseline is { } baseline) running.Add(baseline);
            if (ctx.BaselineTests is { } tests) running.Add(tests);
            if (running.Count > 0)
            {
                try
                {
                    await Task.WhenAll(running).WaitAsync(BaselineStopWait, time, CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    ctx.Messages.Add(ctx.BaselineTests is null
                        ? $"원본 상태 빌드가 {BaselineStopWait.TotalSeconds:0}초 안에 멈추지 않았습니다. 남은 빌드 프로세스가 있는지 확인하세요"
                        : $"원본 상태 빌드·테스트가 {BaselineStopWait.TotalSeconds:0}초 안에 멈추지 않았습니다. 남은 빌드·테스트 프로세스가 있는지 확인하세요");
                }
                catch (Exception)
                {
                    // Cancelled or failed: the result is no longer used.
                }
            }
        }
        finally
        {
            cts.Dispose();
            ctx.BaselineCancellation = null;
        }
    }

    /// <summary>
    /// Restores the original bytes when pelly wrote anything. Never overwrites a change made outside pelly.
    /// Returns the file as left: the restored snapshot, <see cref="RunContext.Current"/> when nothing needed restoring
    /// (null before Preflight read the file), or null when the file was not restored.
    /// </summary>
    private async Task<FileSnapshot?> RollbackAsync(RunContext ctx)
    {
        if (ctx.Original is null) return null;
        if (ctx.BackupPath is null || ctx.Current.Sha256 == ctx.Original.Sha256) return ctx.Current;
        try
        {
            var result = await fileGuard.RestoreAsync(ctx.BackupPath, ctx.Current, CancellationToken.None);
            switch (result.Status)
            {
                case FileWriteStatus.Written:
                    ctx.Current = result.Snapshot!;
                    ctx.Messages.Add($"원래 파일로 되돌렸습니다: {ctx.FilePath}");
                    return ctx.Current;
                case FileWriteStatus.ExternallyChanged:
                    ctx.Messages.Add($"파일이 pelly 밖에서 바뀌어 되돌리지 않았습니다. 원본은 {ctx.BackupPath}에 있습니다.");
                    break;
                default:
                    ctx.Messages.Add($"원래 파일로 되돌리지 못했습니다 ({result.Detail}). 원본은 {ctx.BackupPath}에 있습니다.");
                    break;
            }
        }
        catch (Exception ex)
        {
            ctx.Messages.Add($"원래 파일로 되돌리지 못했습니다 ({ex.GetType().Name}: {ex.Message}). 원본은 {ctx.BackupPath}에 있습니다.");
        }
        return null;
    }

    /// <summary>Failure label for outcomes the phase itself does not label (patch failures carry their own kind).</summary>
    private static string? LabelOf(PhaseOutcome outcome) => outcome switch
    {
        PhaseOutcome.NewErrors => "SyntaxErrors",
        PhaseOutcome.FileChangedExternally => "FileChangedExternally",
        PhaseOutcome.BudgetExceeded => "BudgetExceeded",
        PhaseOutcome.EnvError => "EnvError",
        PhaseOutcome.Rejected => "Rejected",
        PhaseOutcome.Cancelled => "Cancelled",
        PhaseOutcome.Crashed => "Crashed",
        _ => null,
    };

    private string Describe(RunContext ctx, RunPhase phase)
    {
        var file = fs.Path.GetFileName(ctx.Request.FilePath);
        return phase switch
        {
            RunPhase.Preflight => $"사전 점검: {file}",
            RunPhase.Locate when ctx.Request.Kind == EditKind.Sql => ctx.Request switch
            {
                { ObjectName: { } obj } => $"대상 찾기: {obj}",
                { Line: { } line } => $"대상 찾기: {line}번 줄",
                _ => "대상 찾기: 파일에서",
            },
            RunPhase.Locate => ctx.Request switch
            {
                { Line: { } line } => $"대상 찾기: {line}번 줄",
                { MethodName: { } method } => $"대상 찾기: {method}",
                _ => "대상 찾기: 요청문에서",
            },
            RunPhase.Slice => "문맥 구성",
            RunPhase.Edit => $"수정 생성 (시도 {ctx.Attempt + 1}/{1 + ctx.MaxRetries})",
            RunPhase.Apply => ctx.Request.DryRun ? "패치 미리 보기" : "패치 적용",
            RunPhase.Verify when ctx.Request.Kind == EditKind.Sql => "객체 존재·컴파일 검사",
            RunPhase.Verify => ctx.BuildTool is null ? "구문 검사" : ctx.TestProjects.Count > 0 ? "구문 검사·빌드·테스트" : "구문 검사·빌드",
            RunPhase.Review => "변경 검토",
            _ => phase.ToString(),
        };
    }

    /// <summary>
    /// Shows each run message once. Once <see cref="IUserInteraction.Warn"/> throws, display stops for this run
    /// (retrying would throw again) and the failure is kept in the messages for <c>messages.txt</c>.
    /// </summary>
    private sealed class MessageSink(IUserInteraction ui, RunContext ctx)
    {
        private int _shown;
        private bool _broken;

        public void Flush()
        {
            while (!_broken && _shown < ctx.Messages.Count)
            {
                var message = ctx.Messages[_shown++];
                try
                {
                    ui.Warn(message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _broken = true;
                    ctx.Messages.Add($"메시지를 화면에 표시하지 못했습니다: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
