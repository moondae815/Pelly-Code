using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Re-reads the file, re-finds the target batch (an earlier attempt may have rewritten it) and builds the budgeted context:
/// the schema of what the batch references, then the batch itself.
/// </summary>
public sealed class SqlSlicePhase(FileGuard fileGuard, ITokenCounter tokens, BudgetOptions budget, ISqlExecutor sql) : IPhase
{
    public RunPhase Phase => RunPhase.Slice;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var current = fileGuard.Read(ctx.FilePath);
        if (current.Sha256 != ctx.Current.Sha256)
        {
            // Keep ctx.Current as the last content pelly knows, so rollback sees the change and does not overwrite it.
            ctx.Messages.Add($"pelly 실행 중에 파일이 다른 곳에서 바뀌었습니다. 더 진행하지 않습니다: {ctx.FilePath}");
            ctx.FailureKind = "FileChangedExternally";
            return PhaseOutcome.EnvError;
        }
        ctx.Current = current;

        var state = ctx.Sql!;
        var script = SqlBatchSplitter.Split(current.Text, state.CompatLevel);
        if (script.Batches.Count != state.OriginalScript.Batches.Count || state.TargetIndex >= script.Batches.Count)
            return NotFound(ctx, state);

        var batch = script.Batches[state.TargetIndex];
        // A batch the parser rejects was broken by an earlier attempt; APPLY should have refused to write it.
        if (batch.ParseErrors.Count > 0 || batch.ObjectName is null)
            return NotFound(ctx, state);
        state.CurrentTarget = batch;

        var settings = script.SettingsBefore(batch.Index);
        SliceResult slice;
        try
        {
            var refs = SqlReferenceCollector.Collect(batch.Text, batch.ObjectName, state.CompatLevel, settings.QuotedIdentifier);
            slice = await SqlSchemaSlicer.BuildAsync(batch, refs, sql, tokens, budget.Context, state.CompatLevel, ct);
        }
        catch (SqlUnavailableException ex)
        {
            ctx.Messages.Add($"SQL 서버를 사용할 수 없습니다: {ex.Message} (`pelly doctor`로 확인하세요)");
            return PhaseOutcome.EnvError;
        }
        catch (ArgumentException)
        {
            return NotFound(ctx, state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // E.g. a SqlException the executor did not classify; never let it surface as Crashed before the model is called.
            ctx.Messages.Add($"스키마 정보를 가져오지 못했습니다: {SqlConnectionText.MaskInText(ex.Message, ctx.Options.Sql.ConnectionString)}");
            return PhaseOutcome.EnvError;
        }

        ctx.SliceResult = slice;
        ctx.Target = ctx.Target! with { MemberSpan = new TextSpanInfo(batch.StartLine, batch.EndLine) };
        if (slice.TargetTooLarge)
        {
            var targetTokens = slice.Slice.Included.First(p => p.Kind == "target").Tokens;
            ctx.Messages.Add($"대상 객체가 문맥 예산보다 큽니다 ({targetTokens} > budget.context {budget.Context} 토큰). 객체를 나누거나 예산을 늘리세요.");
            return PhaseOutcome.BudgetExceeded;
        }
        return PhaseOutcome.Ok;
    }

    private static PhaseOutcome NotFound(RunContext ctx, SqlRunState state)
    {
        ctx.Messages.Add($"수정 중인 객체 '{state.Target.Display}'을(를) 다시 찾을 수 없습니다.");
        ctx.FailureKind = "TargetNotFound";
        return PhaseOutcome.NotFound;
    }
}
