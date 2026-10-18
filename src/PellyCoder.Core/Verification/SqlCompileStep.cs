using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Compiles the target batch under <c>SET NOEXEC ON</c> and fails only on errors the original batch did not have.
/// The baseline ignores line numbers, so edits that shift lines do not turn old errors into new ones.
/// </summary>
public sealed class SqlCompileStep(ISqlExecutor sql, FeedbackBuilder feedback) : IVerifierStep
{
    public string Name => "SqlCompile";

    public async Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Sql ?? throw new InvalidOperationException("SQL verification needs the SQL run state.");
        var file = Path.GetFileName(ctx.FilePath);
        try
        {
            if (state.OriginalCompileErrors is null)
            {
                var original = state.OriginalScript.Batches[state.TargetIndex];
                state.OriginalCompileErrors = await CompileAsync(original, state, file, ct);
                if (state.OriginalCompileErrors.Count > 0)
                    ctx.Messages.Add($"원본 객체에 이미 컴파일 오류가 {state.OriginalCompileErrors.Count}개 있습니다 — 새로 생긴 오류만 검사합니다");
            }

            var batch = SqlVerifyTarget.Current(ctx, state);
            var current = await CompileAsync(batch, state, file, ct);
            var newErrors = BaselineDiff.New(state.OriginalCompileErrors, current);
            if (newErrors.Count > 0)
                return StepResult.Fail(feedback.ForDiagnostics("SQL CHECK FAILED", newErrors, ctx.Current.Text), newErrors, []);

            if (!state.NoticesShown)
            {
                state.NoticesShown = true;
                AddNotices(ctx, state, batch);
            }
            return StepResult.Pass;
        }
        catch (SqlUnavailableException ex)
        {
            return StepResult.Environment($"SQL 서버를 사용할 수 없습니다: {ex.Message} (`pelly doctor`로 확인하세요)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StepResult.Environment($"SQL 컴파일 검사를 할 수 없습니다: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<Diagnostic>> CompileAsync(SqlBatch batch, SqlRunState state, string file, CancellationToken ct)
    {
        var errors = await sql.CompileWithNoExecAsync(batch.Text, state.Settings, ct);
        return [.. errors.Select(e => new Diagnostic(file, batch.StartLine + e.Line - 1, 0, "error", $"Msg{e.Number}", e.Message))];
    }

    private static void AddNotices(RunContext ctx, SqlRunState state, SqlBatch batch)
    {
        var refs = SqlReferenceCollector.Collect(batch.Text, state.Target, state.CompatLevel, state.Settings.QuotedIdentifier);
        if (refs.HasDynamicSql) ctx.Messages.Add("동적 SQL 안의 문장은 검증하지 않습니다 (설계 §8.10)");
        if (refs.UsesTempTables) ctx.Messages.Add("임시 테이블(#)의 컬럼은 검증하지 않습니다 (설계 §8.10)");
        if (refs.SkippedMultiPart.Count > 0)
            ctx.Messages.Add($"다른 데이터베이스·연결된 서버 객체({string.Join(", ", refs.SkippedMultiPart)})는 존재를 검사하지 않습니다");
    }
}
