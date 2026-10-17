using PellyCoder.Core.Abstractions;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>Shows the full diff against the original and asks for approval (design §8.11). Non-interactive without --yes rejects.</summary>
public sealed class ReviewPhase(IUserInteraction ui) : IPhase
{
    public RunPhase Phase => RunPhase.Review;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        // Informational, so it also shows with --yes: the build cannot check SQL text.
        if (ctx.Candidate is { } member && ctx.Language is { } language
            && InlineSqlDetector.ChangedSqlLiteral(ctx.Original.Text, ctx.Current.Text, member.Display, language.Version))
            ui.Warn("인라인 SQL이 바뀌었습니다 — 빌드는 통과했지만 DB로 검증되지 않았습니다 (설계 §8.15)");

        ui.ShowDiff(ctx.FilePath, ctx.Original.Text, ctx.Current.Text);
        if (ctx.Request.Yes)
            return PhaseOutcome.Approved;

        if (!ui.IsInteractive)
        {
            ctx.Messages.Add("비대화형 실행에서는 --yes 없이 변경을 적용하지 않습니다. 원래 파일로 되돌립니다.");
            return PhaseOutcome.Rejected;
        }

        return await ui.ConfirmAsync("적용할까요?", false, ct) ? PhaseOutcome.Approved : PhaseOutcome.Rejected;
    }
}
