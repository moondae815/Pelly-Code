using System.Text.RegularExpressions;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Verification;

/// <summary>Fails when the code (not comments or literals) of a line added by the model matches any AGENTS.md rule's <c>forbid</c> regex (fail closed on a regex timeout).</summary>
public sealed class RulesStep(FeedbackBuilder feedback) : IVerifierStep
{
    public string Name => "Rules";

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        if (!ctx.Rules.Any(r => r.Forbid is not null)) return Task.FromResult(StepResult.Pass);

        IReadOnlyList<RuleViolation> violations;
        var added = ForbiddenPatternChecker.AddedLines(ctx.Original.Text, ctx.Current.Text, CodeOf(ctx));
        try { violations = ForbiddenPatternChecker.Check(ctx.Rules, added); }
        catch (RegexMatchTimeoutException e)
        {
            var line = ctx.Rules.FirstOrDefault(r => r.Forbid is not null && r.Forbid == e.Pattern)?.Line ?? 0;
            return Task.FromResult(StepResult.Environment(
                $"AGENTS.md 줄 {line}의 forbid 정규식 검사가 시간 안에 끝나지 않았습니다. 정규식을 단순하게 고치세요", FailureKinds.RulesInvalid));
        }
        if (violations.Count == 0) return Task.FromResult(StepResult.Pass);

        var errors = violations.Select(v => new Diagnostic(ctx.FilePath, 0, 0, "error", "RULE", v.Rule.Text)).ToList();
        return Task.FromResult(StepResult.Fail(feedback.ForRuleViolations(violations), errors, []));
    }

    /// <summary>
    /// The patched file with comments and literals masked, so forbid patterns match code only (Roslyn for C#, ScriptDom for SQL).
    /// Null — check the raw text, which is stricter — when the SQL does not lex or masking fails for any reason.
    /// </summary>
    private static string? CodeOf(RunContext ctx)
    {
        try
        {
            if (ctx.Target?.Kind == EditKind.Sql)
                return ctx.Sql is { } sql ? CodeMask.Sql(ctx.Current.Text, sql.CompatLevel, sql.Settings.QuotedIdentifier) : null;
            return CodeMask.CSharp(ctx.Current.Text, ctx.Language?.Version ?? Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
