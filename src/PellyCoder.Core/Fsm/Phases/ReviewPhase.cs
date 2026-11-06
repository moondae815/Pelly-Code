using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Web;

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
        (AspApplyPhase.LogicCheck, "논리"),
    ];

    /// <summary>Shown with every JS/CSS review, also with --yes: no check runs the code in a browser.</summary>
    public const string WebNotice =
        "JS·CSS는 브라우저에서 실행해 확인하지 않았습니다. 화면에서 직접 확인하십시오. 브라우저 캐시 때문에 바뀐 파일이 바로 반영되지 않을 수 있습니다.";

    /// <summary>
    /// JS/CSS checks on the review line, in this order, with their labels. Rules is not listed (the shared rules step records
    /// nothing in <see cref="WebRunState"/>; a violation fails the run anyway). CSS structure is recorded under Syntax.
    /// </summary>
    private static readonly (string Name, string Label)[] WebCheckLabels =
    [
        (WebApplyPhase.EncodingCheck, "인코딩"),
        (WebApplyPhase.SyntaxCheck, "문법"),
        (WebApplyPhase.LevelCheck, "문법 수준"),
        (WebApplyPhase.ShapeCheck, "대상 모양"),
        (WebApplyPhase.SafetyCheck, "안전"),
        ("Names", "이름"),
        ("Api", "API"),
        ("Properties", "속성"),
        (CssPropertyStep.OverrideCheck, "덮어쓰기"),
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
            if (CheckLine(asp.Checks, AspCheckLabels) is { } line)
                Notice(ctx, line);
            Notice(ctx, AspNotice);
        }

        if (ctx.Web is { } web)
        {
            if (CheckLine(web.Checks, WebCheckLabels) is { } line)
                Notice(ctx, line);
            // A pure-ASCII file got non-ASCII text: it is now saved in the configured encoding, which the page must match.
            if (web.OriginalAscii && ctx.Current.Text.AsSpan().IndexOfAnyExceptInRange((char)0, (char)0x7F) >= 0)
                Notice(ctx, $"이 파일은 ASCII였고 {web.EncodingName}로 저장했습니다");
            Notice(ctx, WebNotice);
            if (BlockLocation(ctx, web) is { } location)
                Notice(ctx, location);
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

    /// <summary>「p/list.asp 2–9줄 &lt;script&gt; 블록」 for a page run; null otherwise.</summary>
    private static string? BlockLocation(RunContext ctx, WebRunState web)
    {
        if (web.Page is not { } page) return null;
        var blocks = AspPage.Parse(ctx.Current.Text).ClientBlocks;
        if (page.BlockIndex >= blocks.Count) return null;
        var block = blocks[page.BlockIndex];
        var root = web.SiteRoot.Replace('\\', '/').TrimEnd('/');
        var path = ctx.FilePath.Replace('\\', '/');
        var relative = path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..] : path;
        var tag = block.Kind == AspClientBlockKind.Style ? "<style>" : "<script>";
        return $"{relative} {block.OpenLine}–{block.CloseLine}줄 {tag} 블록";
    }

    /// <summary>Shown now and kept in <c>messages.txt</c>.</summary>
    private void Notice(RunContext ctx, string message)
    {
        ui.Warn(message);
        ctx.Record(message);
    }

    /// <summary>
    /// 「검사: 구조 통과, …, 컴파일 건너뜀(Windows가 아님)」 for the checks in <paramref name="labels"/> that recorded a result, in
    /// that order; null when none did.
    /// </summary>
    private static string? CheckLine(IReadOnlyDictionary<string, AspCheck> checks, (string Name, string Label)[] labels)
    {
        var parts = new List<string>();
        foreach (var (name, label) in labels)
        {
            if (!checks.TryGetValue(name, out var check)) continue;
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
