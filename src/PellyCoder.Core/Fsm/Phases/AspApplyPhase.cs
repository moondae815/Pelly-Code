using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// ASP APPLY (design §4.5 steps 1–4): applies the blocks in memory, then judges the patched page before anything is written —
/// the file encoding must store every new character, the page structure must not gain parse problems, the include list,
/// directives and target procedure must keep their shape, and no new unsafe statement may appear. Each check is recorded
/// in <see cref="AspRunState.Checks"/>. After a write the target span follows the change in line count.
/// </summary>
public sealed class AspApplyPhase(FileGuard fileGuard, FeedbackBuilder feedback, IUserInteraction ui, AspOptions options, ITextFileCodec codec) : IPhase
{
    public const string EncodingCheck = "Encoding";
    public const string StructureCheck = "Structure";
    public const string SafetyCheck = "Safety";

    public RunPhase Phase => RunPhase.Apply;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Asp!;
        var patch = SearchReplacePatcher.Apply(ctx.Current.Text, ctx.Blocks, ctx.SliceResult.Regions);
        if (!patch.Success)
        {
            ctx.Feedback = feedback.ForPatchFailure(patch.Failure!);
            ctx.FailureKind = patch.Failure!.Kind;
            return PhaseOutcome.MatchFailed;
        }
        var newText = patch.NewText!;

        try
        {
            codec.Encode(ctx.Current, newText);
            state.Record(EncodingCheck, AspCheckStatus.Passed);
        }
        catch (UnencodableTextException ex)
        {
            var lineText = LineOf(newText, ex.Line);
            var character = CharacterText(lineText, ex.Character);
            state.Record(EncodingCheck, AspCheckStatus.Failed, $"줄 {ex.Line}: {character}");
            ctx.Feedback = feedback.ForEncoding(state.EncodingName, character, ex.Line, lineText);
            ctx.FailureKind = FailureKinds.AspEncoding;
            return PhaseOutcome.MatchFailed;
        }

        var patched = AspPage.Parse(newText);
        var problems = AspStructureCheck.NewProblems(state.OriginalPage, patched);
        if (problems.Count > 0)
        {
            var file = Path.GetFileName(ctx.FilePath);
            var errors = problems.Select(p => new Diagnostic(file, p.Line, p.Column, "error", p.Code, p.Message)).ToList();
            foreach (var error in errors)
            {
                if (!ctx.ErrorCodes.Contains(error.Code)) ctx.ErrorCodes.Add(error.Code);
            }
            state.Record(StructureCheck, AspCheckStatus.Failed, string.Join(", ", problems.Select(p => p.Code).Distinct()));
            ctx.Feedback = feedback.ForDiagnostics("ASP STRUCTURE CHECK FAILED", errors, newText);
            ctx.FailureKind = FailureKinds.AspStructureErrors;
            return PhaseOutcome.MatchFailed;
        }

        if (AspStructureCheck.ShapeViolation(state.OriginalPage, patched, state) is { } reason)
        {
            state.Record(StructureCheck, AspCheckStatus.Failed, reason);
            ctx.Feedback = feedback.ForShapeRejection(reason);
            ctx.FailureKind = FailureKinds.ShapeRejected;
            return PhaseOutcome.ShapeRejected;
        }
        state.Record(StructureCheck, AspCheckStatus.Passed);

        var findings = AspSafetyGuard.NewFindings(state.OriginalPage, patched, options.SqlSanitizers);
        if (findings.Count > 0)
        {
            state.Record(SafetyCheck, AspCheckStatus.Failed, string.Join(", ", findings.Select(f => f.Rule).Distinct()));
            ctx.Feedback = feedback.ForAspSafety(findings, options.SqlSanitizers);
            ctx.FailureKind = FailureKinds.AspSafetyRejected;
            return PhaseOutcome.SafetyRejected;
        }
        state.Record(SafetyCheck, AspCheckStatus.Passed);

        // Compared with the original, not the last write: undoing an earlier attempt is no change either.
        if (newText == ctx.Original.Text)
        {
            ctx.Feedback = feedback.ForNoChange();
            ctx.FailureKind = FailureKinds.NoChange;
            return PhaseOutcome.MatchFailed;
        }

        if (ctx.Request.DryRun)
        {
            PatchWriter.SetStages(ctx, patch);
            ui.ShowDiff(ctx.FilePath, ctx.Original.Text, newText);
            return PhaseOutcome.DryRunCompleted;
        }

        var before = LineCount(ctx.Current.Text);
        var outcome = await PatchWriter.WriteAsync(ctx, fileGuard, feedback, patch, ct);
        if (outcome == PhaseOutcome.Ok)
        {
            // Only the editable regions change, so the whole file's line delta is the target's.
            var span = state.CurrentSpan;
            state.CurrentSpan = span with { EndLine = span.EndLine + LineCount(ctx.Current.Text) - before };
        }
        return outcome;
    }

    /// <summary>Lines of LF text; a final line feed ends the last line.</summary>
    private static int LineCount(string textLf)
    {
        if (textLf.Length == 0) return 0;
        var breaks = textLf.AsSpan().Count('\n');
        return textLf.EndsWith('\n') ? breaks : breaks + 1;
    }

    private static string LineOf(string textLf, int line)
    {
        var lines = textLf.Split('\n');
        return line >= 1 && line <= lines.Length ? lines[line - 1] : "";
    }

    /// <summary>The bad character as text: a high surrogate is shown with its low half taken from the line.</summary>
    private static string CharacterText(string lineText, char bad)
    {
        if (!char.IsHighSurrogate(bad)) return bad.ToString();
        var at = lineText.IndexOf(bad);
        return at >= 0 && at + 1 < lineText.Length && char.IsLowSurrogate(lineText[at + 1]) ? lineText.Substring(at, 2) : bad.ToString();
    }
}
