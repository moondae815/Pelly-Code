using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>Shows the full diff against the original and asks for approval (design §8.11). Non-interactive without --yes rejects.</summary>
public sealed class ReviewPhase(IUserInteraction ui) : IPhase
{
    /// <summary>Shown with every ASP review, also with --yes: no check runs the page.</summary>
    public const string AspNotice = "ASP는 실행해서 확인하지 않았습니다. 화면에서 직접 확인하십시오.";

    /// <summary>
    /// ASP checks on the review line, in this order, with their labels. Rules is not listed (the shared rules step records
    /// nothing in <see cref="AspRunState"/>; a violation fails the run anyway), nor SqlContext (prompt context, not a check).
    /// </summary>
    private static readonly (string Name, string Label)[] AspCheckLabels =
    [
        (AspApplyPhase.StructureCheck, "구조"),
        (AspApplyPhase.SafetyCheck, "안전"),
        (AspApplyPhase.EncodingCheck, "인코딩"),
        ("AspNames", "이름"),
        ("AspCompile", "컴파일"),
        ("AspSp", "SP"),
    ];

    public RunPhase Phase => RunPhase.Review;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        // Informational, so it also shows with --yes: the build cannot check SQL text.
        if (ctx.Candidate is { } member && ctx.Language is { } language
            && InlineSqlDetector.ChangedSqlLiteral(ctx.Original.Text, ctx.Current.Text, member.Display, language.Version))
            ui.Warn("인라인 SQL이 바뀌었습니다 — 빌드는 통과했지만 DB로 검증되지 않았습니다 (설계 §8.15)");

        if (ctx.Asp is { } asp)
        {
            if (AspCheckLine(asp) is { } line)
            {
                ui.Warn(line);
                ctx.Record(line);
            }
            ui.Warn(AspNotice);
            ctx.Record(AspNotice);
        }

        ui.ShowDiff(ctx.FilePath, ctx.Original.Text, ctx.Current.Text);
        if (ctx.Request.Yes)
            return PhaseOutcome.Approved;

        if (!ui.IsInteractive)
        {
            ctx.Messages.Add("비대화형 실행에서는 --yes 없이 변경을 적용하지 않습니다. 원래 파일로 되돌립니다.");
            return PhaseOutcome.Rejected;
        }

        return await ui.ApproveAsync("적용할까요?", ct) ? PhaseOutcome.Approved : PhaseOutcome.Rejected;
    }

    /// <summary>「검사: 구조 통과, …, 컴파일 건너뜀(Windows가 아님)」 for the checks that recorded a result; null when none did.</summary>
    private static string? AspCheckLine(AspRunState asp)
    {
        var parts = new List<string>();
        foreach (var (name, label) in AspCheckLabels)
        {
            if (!asp.Checks.TryGetValue(name, out var check)) continue;
            var status = check.Status switch
            {
                AspCheckStatus.Passed => "통과",
                AspCheckStatus.Warned => "경고",
                AspCheckStatus.Skipped => "건너뜀",
                _ => "실패",
            };
            parts.Add(string.IsNullOrEmpty(check.Detail) || check.Status == AspCheckStatus.Passed
                ? $"{label} {status}"
                : $"{label} {status}({check.Detail})");
        }
        return parts.Count == 0 ? null : "검사: " + string.Join(", ", parts);
    }
}
