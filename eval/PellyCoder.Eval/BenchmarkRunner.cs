using System.Diagnostics;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Eval;

/// <summary>What a runner needs besides the task: the real (or canned) model, options and the SQL connection, if any.</summary>
/// <param name="SqlSkipReason">Non-null when SQL tasks cannot run (no connection, or the target is not allow-listed).</param>
public sealed record RunnerSetup(
    IFileSystem Fs, IProcessRunner Processes, ILlmBackend Backend, PellyOptions Options, TimeProvider Time, string EvalRoot,
    ISqlExecutor? Sql, string? SqlSkipReason, Action<string> Log, Func<bool>? IsWindows = null,
    IIndependentVerifier? Verifier = null);

/// <summary>
/// Runs one benchmark task end to end (design §10.6): copy the corpus into <c>&lt;out&gt;/work/&lt;id&gt;/</c>, restore
/// (Modern, not timed), run the orchestrator with <c>--yes</c> (timed), read the run's logs, re-verify independently, judge.
/// </summary>
public sealed class BenchmarkRunner(RunnerSetup setup)
{
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(5);

    public async Task<TaskResult> RunAsync(BenchmarkTask task, string outDir, CancellationToken ct)
    {
        var isSql = task.Kind == "sql";
        if (isSql && setup.SqlSkipReason is not null)
            return TaskResult.Skipped(task, setup.SqlSkipReason);

        var workRoot = Path.Combine(outDir, "work", task.Id);
        if (Directory.Exists(workRoot))
            return TaskResult.Skipped(task, $"작업 폴더가 이미 있습니다: {workRoot}");
        var file = WorkCopy.Prepare(setup.EvalRoot, task, workRoot);
        var corpusRoot = Path.Combine(workRoot, task.File.Split('/')[1]);

        var originalText = TextFileCodec.Read(setup.Fs, file).Text;
        var display = !isSql && task.Method is null && task.Line is { } line ? TargetText.DisplayAtLine(originalText, line) : null;
        var originalTarget = Target(task, originalText, display);
        if (originalTarget is null)
            return TaskResult.Failed(task, "원본에서 대상을 찾지 못함 (작업 정의 오류)", "Definition");

        if (Directory.GetFiles(corpusRoot, "*.sln").FirstOrDefault() is { } sln && !task.IsLegacy)
        {
            var restore = await setup.Processes.RunAsync(
                new ProcessSpec("dotnet", ["restore", sln, "--nologo", "--disable-parallel"], corpusRoot, RestoreTimeout), ct);
            if (restore.ExitCode != 0)
                return TaskResult.EnvironmentSkipped(task, "복원 실패", $"dotnet restore 종료 {restore.ExitCode}");
        }

        var ui = new EvalUserInteraction(task.Id, setup.Log);
        var request = new RunRequest(file, null, task.Method, task.Line, task.Request, Yes: true, DryRun: false, MaxRetries: null,
            ObjectName: task.Object, Kind: isSql ? EditKind.Sql : EditKind.CSharp);
        var orchestrator = isSql
            ? FsmOrchestratorFactory.CreateSql(setup.Fs, setup.Backend, ui, setup.Options, setup.Time, setup.Sql!)
            : FsmOrchestratorFactory.Create(setup.Fs, setup.Backend, ui, setup.Options, setup.Time, setup.Processes, setup.IsWindows);

        setup.Log($"[{task.Id}] 실행: {task.Request}");
        var watch = Stopwatch.StartNew();
        var exit = await orchestrator.RunAsync(request, ct);
        var wall = watch.Elapsed.TotalSeconds;

        var artifacts = RunArtifacts.Read(WorkspaceLocator.Resolve(setup.Fs, file).PellyDir);
        var finalTarget = Target(task, TextFileCodec.Read(setup.Fs, file).Text, display);

        var independent = exit != 0
            ? VerificationResult.Skip("실행 실패로 독립 검증 생략")
            : await VerifyAsync(task, file, corpusRoot, isSql, ct);

        var outcome = TaskJudge.Judge(task, originalTarget, finalTarget ?? "", exit, independent);
        var reasons = outcome.Reasons.ToList();
        if (finalTarget is null) reasons.Add("수정 후 대상을 찾지 못함");
        var passed = outcome.Passed && finalTarget is not null;

        string? failureType = exit != 0 ? artifacts.FailureKind ?? $"Exit{exit}"
            : outcome.InvariantViolated ? "InvariantViolated"
            : passed ? null
            : outcome.JudgeToolingSkipped ? "JudgeTooling"
            : "Judge";

        var result = new TaskResult(task.Id, task.Kind, passed ? VerificationResult.Passed : VerificationResult.Failed, exit,
            artifacts.Attempts, reasons, independent, outcome.BuildCheck, outcome.InvariantViolated, failureType,
            wall, artifacts.TtftSeconds, artifacts.ThinkTokens, artifacts.ThinkTruncated, artifacts.EditOutcomes, null, task.IsLegacy, artifacts.FormatErrors);
        setup.Log($"[{task.Id}] {result.Status} (종료 {exit}, 시도 {result.Attempts}, {wall:0.0}초, 독립 검증 {independent.Status})"
            + (reasons.Count > 0 ? " — " + string.Join("; ", reasons) : ""));
        return result;
    }

    private string? Target(BenchmarkTask task, string textLf, string? display) => task.Kind == "sql"
        ? TargetText.ForSql(textLf, task.Object, setup.Options.Sql.CompatLevel)
        : TargetText.ForCs(textLf, task.Method, task.Line, display);

    /// <summary>
    /// The run is over and its exit code stands: an error of the check itself (not of the edit) is recorded as a skipped
    /// check, so it neither fakes an invariant violation nor loses the row.
    /// </summary>
    private async Task<VerificationResult> VerifyAsync(BenchmarkTask task, string file, string corpusRoot, bool isSql, CancellationToken ct)
    {
        var verifier = setup.Verifier ?? new IndependentVerifier(setup.Fs, setup.Processes, setup.Options, setup.Sql, setup.IsWindows, setup.EvalRoot);
        try
        {
            return isSql ? await verifier.VerifySqlAsync(task, file, ct) : await verifier.VerifyCsAsync(task, file, corpusRoot, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return VerificationResult.Skip($"독립 검증 오류: {ex.Message}");
        }
    }
}
