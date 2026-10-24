using System.Text.RegularExpressions;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Verification;

/// <summary>Fails when a line added by the model matches any AGENTS.md rule's <c>forbid</c> regex (fail closed on a regex timeout).</summary>
public sealed class RulesStep(FeedbackBuilder feedback) : IVerifierStep
{
    public string Name => "Rules";

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        if (!ctx.Rules.Any(r => r.Forbid is not null)) return Task.FromResult(StepResult.Pass);

        IReadOnlyList<RuleViolation> violations;
        try { violations = ForbiddenPatternChecker.Check(ctx.Rules, ForbiddenPatternChecker.AddedLines(ctx.Original.Text, ctx.Current.Text)); }
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
}
