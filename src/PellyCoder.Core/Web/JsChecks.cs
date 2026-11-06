using Acornima;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Web;

/// <summary>
/// Result of <see cref="JsChecks.Syntax"/>. <see cref="Patched"/> is the parsed patched file (null in fallback mode or when it
/// does not parse); with <see cref="LevelExceeded"/> it is the file parsed at the latest level.
/// </summary>
public sealed record JsSyntaxVerdict(JsDocument? Patched, IReadOnlyList<Diagnostic> Errors, bool LevelExceeded, string? Feature, int? FeatureLine, string? FeatureEnglish = null);

/// <summary>Blocking JS checks run on a patched file; everything is counted as new compared with the original, by content.</summary>
public static class JsChecks
{
    public static JsSyntaxVerdict Syntax(WebRunState state, string fileName, string patchedText)
    {
        if (state.Fallback) return FallbackSyntax(state.OriginalText, fileName, patchedText);
        return Syntax(state.TargetLevel ?? EcmaVersion.Latest, state.Module, fileName, patchedText);
    }

    /// <summary>The parse check at <paramref name="target"/>: a parse error at the latest level is a syntax error, one only below it a level excess.</summary>
    public static JsSyntaxVerdict Syntax(EcmaVersion target, bool module, string fileName, string patchedText)
    {
        var doc = JsDocument.Parse(patchedText, target, module);
        if (doc.Error is null) return new JsSyntaxVerdict(doc, [], false, null, null);

        var latest = JsDocument.Parse(patchedText, EcmaVersion.Latest, module);
        if (latest.Error is null)
        {
            if (JsFeatures.FirstAbove(patchedText, module, target) is { } hit)
                return new JsSyntaxVerdict(latest, [], true, hit.Feature.Name, hit.Line, hit.Feature.EnglishName);
            return new JsSyntaxVerdict(latest, [], true, doc.Error.Description, doc.Error.Line, doc.Error.Description);
        }

        var e = latest.Error;
        return new JsSyntaxVerdict(null, [new Diagnostic(fileName, e.Line, e.Column, "error", "JS", e.Description)], false, null, null);
    }

    /// <summary>The bracket/string/comment pairing check for an original that does not parse: only problems beyond the original's count.</summary>
    public static JsSyntaxVerdict FallbackSyntax(string originalText, string fileName, string patchedText)
    {
        var baseline = JsTokenScan.Problems(originalText)
            .GroupBy(p => (p.Code, p.Message)).ToDictionary(g => g.Key, g => g.Count());
        var errors = new List<Diagnostic>();
        foreach (var group in JsTokenScan.Problems(patchedText).GroupBy(p => (p.Code, p.Message)))
        {
            var extra = group.Count() - baseline.GetValueOrDefault(group.Key);
            if (extra <= 0) continue;
            foreach (var p in group.OrderBy(p => p.Line).TakeLast(extra))
                errors.Add(new Diagnostic(fileName, p.Line, 1, "error", p.Code, p.Message));
        }
        return new JsSyntaxVerdict(null, errors.OrderBy(d => d.Line).ToArray(), false, null, null);
    }

    /// <summary>Why the patched file changed the target's shape (name, parameters, new top-level names); null when it did not.</summary>
    public static string? ShapeViolation(WebRunState state, JsDocument? patched, string patchedText)
    {
        if (state.Unit != WebTargetUnit.Function || state.Function is null) return null;
        var target = state.Function;
        var name = target.QualifiedName;

        IReadOnlyList<JsFunction> functions;
        if (state.Fallback) functions = JsTokenScan.FunctionDeclarations(patchedText);
        else if (patched is null || patched.Error is not null) return null; // syntax is reported by Syntax
        else functions = patched.Functions;

        // Same-named functions (two constructors' this.init, a name declared twice) are allowed: the count must stay the same
        // and the one at the target's position among them (as WebSlicePhase re-finds it) keeps its parameters.
        var originals = (state.Fallback ? null : state.OriginalJs?.Functions) ?? JsTokenScan.FunctionDeclarations(state.OriginalText);
        var was = originals.Where(f => f.QualifiedName == name).ToList();
        var same = functions.Where(f => f.QualifiedName == name).ToList();
        var (expected, index) = Occurrence(was.Count, was.FindIndex(f => f.DeclLine == target.DeclLine));
        if (same.Count != expected) return $"함수 {name}를 찾을 수 없습니다(이름을 바꾸지 마십시오)";
        var now = same[index];
        if (!now.Parameters.SequenceEqual(target.Parameters))
            return $"함수 {name}의 매개변수를 바꿀 수 없습니다: ({string.Join(", ", target.Parameters)}) → ({string.Join(", ", now.Parameters)})";

        if (!state.Fallback && patched is not null && state.OriginalJs is { } original)
        {
            var added = patched.TopLevelNames.Where(n => !original.TopLevelNames.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (added.Count > 0) return $"함수 밖에 새 최상위 선언을 만들 수 없습니다: {string.Join(", ", added)}";
        }
        return null;
    }

    /// <summary>
    /// How many same-named targets must remain and which of them is the target: the original count and the target's index
    /// among them; exactly one at index 0 when the original target is not among them.
    /// </summary>
    internal static (int Expected, int Index) Occurrence(int originalCount, int originalIndex) =>
        originalIndex < 0 || originalIndex >= originalCount ? (1, 0) : (originalCount, originalIndex);
}
