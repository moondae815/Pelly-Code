using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>Re-reads the file, re-finds the member (lines may have moved on a retry) and builds the budgeted context.</summary>
public sealed class SlicePhase(FileGuard fileGuard, ITokenCounter tokens, BudgetOptions budget) : IPhase
{
    public RunPhase Phase => RunPhase.Slice;

    public Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var current = fileGuard.Read(ctx.FilePath);
        if (current.Sha256 != ctx.Current.Sha256)
        {
            // Keep ctx.Current as the last content pelly knows, so rollback sees the change and does not overwrite it.
            ctx.Messages.Add($"pelly 실행 중에 파일이 다른 곳에서 바뀌었습니다. 더 진행하지 않습니다: {ctx.FilePath}");
            ctx.FailureKind = "FileChangedExternally";
            return Task.FromResult(PhaseOutcome.EnvError);
        }
        ctx.Current = current;

        var language = ctx.Language!;
        var previous = ctx.Candidate!;
        // Re-find by the requested name, or by the name of the member LOCATE resolved (--line, request text or model pick).
        var found = ctx.Request.MethodName is { } method
            ? MemberLocator.FindByName(current.Text, language.Version, ctx.Request.ClassName, method)
            : MemberLocator.Locatable(current.Text, language.Version)
                .Where(c => c.ContainingType == previous.ContainingType && MemberLocator.NameOf(c.Display) == MemberLocator.NameOf(previous.Display))
                .ToList();
        var same = found.Where(c => c.Display == previous.Display).ToList();
        var candidate = same.Count == 1 ? same[0] : found.Count == 1 ? found[0] : null;
        if (candidate is null)
        {
            ctx.Messages.Add($"수정 중인 멤버 '{previous.Display}'을(를) 다시 찾을 수 없습니다.");
            ctx.FailureKind = "TargetNotFound";
            return Task.FromResult(PhaseOutcome.NotFound);
        }
        ctx.Candidate = candidate;
        ctx.Target = ctx.Target! with { SymbolDisplay = candidate.Display, MemberSpan = candidate.Span };

        var slice = RoslynSlicer.Build(current.Text, candidate, language, tokens, budget.Context, ctx.AddedHelpers);
        ctx.SliceResult = slice;
        if (slice.TargetTooLarge)
        {
            var targetTokens = slice.Slice.Included.First(p => p.Kind == "target").Tokens;
            ctx.Messages.Add($"대상 멤버가 문맥 예산보다 큽니다 ({targetTokens} > budget.context {budget.Context} 토큰). 더 작은 멤버를 지정하세요.");
            return Task.FromResult(PhaseOutcome.BudgetExceeded);
        }
        return Task.FromResult(PhaseOutcome.Ok);
    }
}
