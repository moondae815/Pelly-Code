using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Models;
using Diagnostic = PellyCoder.Core.Models.Diagnostic;

namespace PellyCoder.Core.CSharp;

/// <summary>Syntax errors already in the original file, with its text so a match can prefer the same source line.</summary>
public sealed record SyntaxBaseline(string TextLf, IReadOnlyList<Diagnostic> Errors)
{
    public static readonly SyntaxBaseline Empty = new("", []);

    public static SyntaxBaseline Of(string textLf, LanguageVersion version, string displayPath) =>
        new(textLf, SyntaxVerifier.Errors(textLf, version, displayPath));
}

/// <summary>Cheap pre-check: parse only, report syntax errors in source order (English messages).</summary>
public static class SyntaxVerifier
{
    /// <summary>All syntax errors of <paramref name="textLf"/>, in source order.</summary>
    public static IReadOnlyList<Diagnostic> Errors(string textLf, LanguageVersion version, string displayPath)
    {
        var tree = CSharpSyntaxTree.ParseText(textLf, new CSharpParseOptions(version));
        return tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .OrderBy(d => d.Location.SourceSpan.Start)
            .Select(d =>
            {
                var pos = d.Location.GetLineSpan().StartLinePosition;
                return new Diagnostic(displayPath, pos.Line + 1, pos.Character + 1, "error", d.Id,
                    d.GetMessage(CultureInfo.InvariantCulture));
            })
            .ToList();
    }

    /// <summary>
    /// Syntax errors of <paramref name="textLf"/> that are not in <paramref name="baseline"/>: the multiset difference
    /// keyed by (code, message), line numbers ignored (design §8.9). Which of several same-key errors counts as new is
    /// decided by source line text: an error on a line whose text also carried that error in the original is matched
    /// first, so the feedback points at the edit rather than at an old error that merely moved.
    /// </summary>
    public static VerifyOutcome Verify(string textLf, LanguageVersion version, string displayPath, SyntaxBaseline? baseline = null)
    {
        baseline ??= SyntaxBaseline.Empty;
        var current = Errors(textLf, version, displayPath);
        var currentLines = textLf.Split('\n');
        var baselineLines = baseline.TextLf.Split('\n');

        var byKey = Count(baseline.Errors, e => (e.Code, e.Message));
        var byKeyAndLine = Count(baseline.Errors, e => (e.Code, e.Message, LineText(baselineLines, e.Line)));
        var matched = new bool[current.Count];

        // Pass 1: same key on a line with the same text. Pass 2: same key anywhere.
        for (var i = 0; i < current.Count; i++)
        {
            var e = current[i];
            var keyLine = (e.Code, e.Message, LineText(currentLines, e.Line));
            if (Take(byKeyAndLine, keyLine) && Take(byKey, (e.Code, e.Message))) matched[i] = true;
        }
        for (var i = 0; i < current.Count; i++)
        {
            if (!matched[i] && Take(byKey, (current[i].Code, current[i].Message))) matched[i] = true;
        }

        var newErrors = current.Where((_, i) => !matched[i]).ToList();
        return new VerifyOutcome(newErrors.Count == 0, newErrors, [], []);
    }

    private static Dictionary<TKey, int> Count<TKey>(IEnumerable<Diagnostic> errors, Func<Diagnostic, TKey> key)
        where TKey : notnull =>
        errors.GroupBy(key).ToDictionary(g => g.Key, g => g.Count());

    private static bool Take<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
    {
        if (!counts.TryGetValue(key, out var left) || left == 0) return false;
        counts[key] = left - 1;
        return true;
    }

    private static string LineText(string[] lines, int line) => line >= 1 && line <= lines.Length ? lines[line - 1].Trim() : "";
}
