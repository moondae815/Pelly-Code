using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Checks that every same-database object the target batch references exists, counting only names that the original
/// batch did not already miss (design §8.10). Three- and four-part names are not checked (R7).
/// </summary>
public sealed class SqlObjectStep(ISqlExecutor sql, FeedbackBuilder feedback) : IVerifierStep
{
    public string Name => "SqlObjects";

    public async Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Sql ?? throw new InvalidOperationException("SQL verification needs the SQL run state.");
        try
        {
            if (state.OriginalMissing is null)
            {
                var original = state.OriginalScript.Batches[state.TargetIndex];
                var originalRefs = SqlReferenceCollector.Collect(original.Text, state.Target, state.CompatLevel, state.Settings.QuotedIdentifier);
                state.OriginalMissing = await sql.FindMissingAsync([.. originalRefs.Objects.Select(o => o.Name)], ct);
                if (state.OriginalMissing.Count > 0)
                    ctx.Messages.Add($"원본 객체가 이미 없는 객체({string.Join(", ", state.OriginalMissing.Select(m => m.Display))})를 참조합니다 — 새로 생긴 것만 검사합니다");
            }

            var batch = SqlVerifyTarget.Current(ctx, state);
            var refs = SqlReferenceCollector.Collect(batch.Text, state.Target, state.CompatLevel, state.Settings.QuotedIdentifier);
            var missing = await sql.FindMissingAsync([.. refs.Objects.Select(o => o.Name)], ct);
            var added = missing.Where(m => !state.OriginalMissing.Any(o => o.SameAs(m))).ToList();
            if (added.Count == 0) return StepResult.Pass;

            var file = Path.GetFileName(ctx.FilePath);
            var errors = new List<Diagnostic>();
            foreach (var name in added)
            {
                var reference = refs.Objects.First(o => o.Name.SameAs(name));
                errors.Add(new Diagnostic(file, batch.StartLine + reference.Line - 1, reference.Column, "error", "Msg208",
                    $"Invalid object name '{name.Display}'."));
            }
            return StepResult.Fail(feedback.ForDiagnostics("SQL CHECK FAILED", errors, ctx.Current.Text), errors, []);
        }
        catch (SqlUnavailableException ex)
        {
            return StepResult.Environment($"SQL 서버를 사용할 수 없습니다: {ex.Message} (`pelly doctor`로 확인하세요)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StepResult.Environment($"SQL 객체 존재 검사를 할 수 없습니다: {ex.Message}");
        }
    }
}
