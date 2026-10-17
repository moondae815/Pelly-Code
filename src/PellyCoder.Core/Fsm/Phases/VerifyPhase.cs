using System.IO.Abstractions;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Feedbacks;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>M1 verification: Roslyn syntax check of the written file against the original's errors (build verification is M2).</summary>
public sealed class VerifyPhase(IFileSystem fs, FeedbackBuilder feedback) : IPhase
{
    private const string Header = "SYNTAX CHECK FAILED";

    public RunPhase Phase => RunPhase.Verify;

    public Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var result = SyntaxVerifier.Verify(ctx.Current.Text, ctx.Language!.Version, fs.Path.GetFileName(ctx.FilePath), ctx.OriginalSyntax);
        if (result.Passed)
            return Task.FromResult(PhaseOutcome.Ok);

        foreach (var code in result.NewErrors.Select(e => e.Code))
        {
            if (!ctx.ErrorCodes.Contains(code)) ctx.ErrorCodes.Add(code);
        }
        ctx.Feedback = feedback.ForDiagnostics(Header, result.NewErrors, ctx.Current.Text);
        return Task.FromResult(PhaseOutcome.NewErrors);
    }
}
