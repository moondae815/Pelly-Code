using System.IO.Abstractions;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Fsm;

namespace PellyCoder.Core.Verification;

/// <summary>Roslyn syntax check of the written file against the original's errors; cheap, so it runs before the build.</summary>
public sealed class SyntaxStep(IFileSystem fs, FeedbackBuilder feedback) : IVerifierStep
{
    private const string Header = "SYNTAX CHECK FAILED";

    public string Name => "Syntax";

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var result = SyntaxVerifier.Verify(ctx.Current.Text, ctx.Language!.Version, fs.Path.GetFileName(ctx.FilePath), ctx.OriginalSyntax);
        return Task.FromResult(result.Passed
            ? StepResult.Pass
            : StepResult.Fail(feedback.ForDiagnostics(Header, result.NewErrors, ctx.Current.Text), result.NewErrors, []));
    }
}
