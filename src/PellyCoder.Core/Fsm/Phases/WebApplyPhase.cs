using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// JS/CSS APPLY: applies the blocks in memory, then judges the patched file before anything is written. JS: encoding, syntax,
/// language level, target shape, safety. CSS: encoding, structure, shape, safety. Each check is recorded in
/// <see cref="WebRunState.Checks"/>. After a write the target span follows the change in line count.
/// </summary>
public sealed class WebApplyPhase(FileGuard fileGuard, FeedbackBuilder feedback, IUserInteraction ui, ITextFileCodec codec) : IPhase
{
    public const string EncodingCheck = "Encoding";
    public const string SyntaxCheck = "Syntax";
    public const string LevelCheck = "Level";
    public const string ShapeCheck = "Shape";
    public const string SafetyCheck = "Safety";

    public RunPhase Phase => RunPhase.Apply;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Web!;
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
            ctx.FailureKind = FailureKinds.WebEncoding;
            return PhaseOutcome.MatchFailed;
        }

        var rejected = state.Kind == EditKind.Css ? JudgeCss(ctx, state, newText) : JudgeJs(ctx, state, newText);
        if (rejected is { } outcome) return outcome;

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
        var written = await PatchWriter.WriteAsync(ctx, fileGuard, feedback, patch, ct);
        if (written == PhaseOutcome.Ok)
        {
            var span = state.CurrentSpan;
            state.CurrentSpan = span with { EndLine = span.EndLine + LineCount(ctx.Current.Text) - before };
        }
        return written;
    }

    /// <summary>Null when every JS check passed; otherwise the outcome to return (feedback and failure kind already set).</summary>
    private PhaseOutcome? JudgeJs(RunContext ctx, WebRunState state, string newText)
    {
        var file = Path.GetFileName(ctx.FilePath);
        var verdict = JsChecks.Syntax(state, file, newText);
        if (verdict.Errors.Count > 0)
        {
            foreach (var error in verdict.Errors)
            {
                if (!ctx.ErrorCodes.Contains(error.Code)) ctx.ErrorCodes.Add(error.Code);
            }
            var first = verdict.Errors[0];
            state.Record(SyntaxCheck, AspCheckStatus.Failed, $"줄 {first.Line}: {first.Message}");
            ctx.Feedback = feedback.ForDiagnostics("JS SYNTAX CHECK FAILED", verdict.Errors, newText);
            ctx.FailureKind = FailureKinds.JsSyntaxErrors;
            return PhaseOutcome.MatchFailed;
        }

        if (state.Fallback)
        {
            state.Record(SyntaxCheck, AspCheckStatus.Skipped, state.FallbackReason);
            state.Record(LevelCheck, AspCheckStatus.Skipped, state.FallbackReason);
        }
        else
        {
            state.Record(SyntaxCheck, AspCheckStatus.Passed);
            if (verdict.LevelExceeded && state.TargetLevel is { } target)
            {
                var feature = verdict.Feature ?? "newer syntax";
                var line = verdict.FeatureLine ?? 1;
                state.LevelRejections++;
                state.Record(LevelCheck, AspCheckStatus.Failed, $"{feature} (줄 {line})");
                ctx.Feedback = feedback.ForJsLevel(JsLevels.Name(target), verdict.FeatureEnglish ?? feature, line, LineOf(newText, line));
                ctx.FailureKind = FailureKinds.JsLevelExceeded;
                return PhaseOutcome.MatchFailed;
            }
            state.Record(LevelCheck, state.TargetLevel is null ? AspCheckStatus.Skipped : AspCheckStatus.Passed);
        }

        if (JsChecks.ShapeViolation(state, verdict.Patched, newText) is { } reason)
        {
            state.Record(ShapeCheck, AspCheckStatus.Failed, reason);
            ctx.Feedback = feedback.ForShapeRejection(reason);
            ctx.FailureKind = FailureKinds.JsTargetChanged;
            return PhaseOutcome.ShapeRejected;
        }
        state.Record(ShapeCheck, AspCheckStatus.Passed);

        var findings = state.Fallback || state.OriginalJs is null || verdict.Patched is null
            ? JsSafetyGuard.NewFindingsText(state.OriginalText, newText)
            : JsSafetyGuard.NewFindings(state.OriginalJs, verdict.Patched);
        if (findings.Count > 0)
        {
            state.Record(SafetyCheck, AspCheckStatus.Failed, string.Join(", ", findings.Select(f => f.Rule).Distinct()));
            ctx.Feedback = feedback.ForWebSafety(findings);
            ctx.FailureKind = FailureKinds.JsSafetyRejected;
            return PhaseOutcome.SafetyRejected;
        }
        state.Record(SafetyCheck, AspCheckStatus.Passed);
        return null;
    }

    /// <summary>Null when every CSS check passed; otherwise the outcome to return (feedback and failure kind already set).</summary>
    private PhaseOutcome? JudgeCss(RunContext ctx, WebRunState state, string newText)
    {
        var original = state.OriginalCss ?? CssSheet.Parse(state.OriginalText);
        var patched = CssSheet.Parse(newText);
        var problems = CssChecks.NewProblems(original, patched);
        if (problems.Count > 0)
        {
            var file = Path.GetFileName(ctx.FilePath);
            var errors = problems.Select(p => new Diagnostic(file, p.Line, p.Column, "error", p.Code, p.Message)).ToList();
            foreach (var error in errors)
            {
                if (!ctx.ErrorCodes.Contains(error.Code)) ctx.ErrorCodes.Add(error.Code);
            }
            state.Record(SyntaxCheck, AspCheckStatus.Failed, string.Join(", ", problems.Select(p => p.Code).Distinct()));
            ctx.Feedback = feedback.ForDiagnostics("CSS STRUCTURE CHECK FAILED", errors, newText);
            ctx.FailureKind = FailureKinds.CssStructureErrors;
            return PhaseOutcome.MatchFailed;
        }
        state.Record(SyntaxCheck, AspCheckStatus.Passed);

        if (CssChecks.ShapeViolation(state, patched) is { } reason)
        {
            state.Record(ShapeCheck, AspCheckStatus.Failed, reason);
            ctx.Feedback = feedback.ForShapeRejection(reason);
            ctx.FailureKind = FailureKinds.CssTargetChanged;
            return PhaseOutcome.ShapeRejected;
        }
        state.Record(ShapeCheck, AspCheckStatus.Passed);

        var findings = CssSafetyGuard.NewFindings(original, patched);
        if (findings.Count > 0)
        {
            state.Record(SafetyCheck, AspCheckStatus.Failed, string.Join(", ", findings.Select(f => f.Rule).Distinct()));
            ctx.Feedback = feedback.ForWebSafety(findings);
            ctx.FailureKind = FailureKinds.CssSafetyRejected;
            return PhaseOutcome.SafetyRejected;
        }
        state.Record(SafetyCheck, AspCheckStatus.Passed);
        return null;
    }

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

    private static string CharacterText(string lineText, char bad)
    {
        if (!char.IsHighSurrogate(bad)) return bad.ToString();
        var at = lineText.IndexOf(bad);
        return at >= 0 && at + 1 < lineText.Length && char.IsLowSurrogate(lineText[at + 1]) ? lineText.Substring(at, 2) : bad.ToString();
    }
}
