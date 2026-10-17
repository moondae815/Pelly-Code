using System.Text.RegularExpressions;

namespace PellyCoder.Core.CSharp;

/// <summary>Identifier candidates named in a change request (design 8.4, step 4).</summary>
public static partial class IdentifierExtractor
{
    /// <summary>
    /// Text in backticks or double quotes, and PascalCase words, in order of appearance without duplicates. A quoted
    /// <c>A.B</c> also yields its last segment <c>B</c>. PascalCase words inside a quoted span are not repeated on their own
    /// (<c>`OrderService.CancelAsync`</c> must not also name the <c>OrderService</c> constructor).
    /// </summary>
    public static IReadOnlyList<string> Candidates(string request)
    {
        var found = new List<(int Index, string Text)>();
        var quoted = new List<(int Start, int End)>();
        foreach (var regex in new[] { Backtick(), DoubleQuote() })
        {
            foreach (Match m in regex.Matches(request))
            {
                quoted.Add((m.Index, m.Index + m.Length));
                var text = m.Groups[1].Value;
                found.Add((m.Index, text));
                var dot = text.LastIndexOf('.');
                if (dot >= 0 && dot < text.Length - 1)
                    found.Add((m.Index, text[(dot + 1)..]));
            }
        }
        foreach (Match m in PascalCase().Matches(request))
        {
            if (!quoted.Any(q => q.Start <= m.Index && m.Index < q.End))
                found.Add((m.Index, m.Value));
        }

        // OrderBy is stable: a quoted name stays ahead of its own last segment.
        return found.OrderBy(f => f.Index).Select(f => f.Text).Distinct(StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex Backtick();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex DoubleQuote();

    // ECMAScript makes \b ASCII-only, so a Korean particle glued to the name ("CancelAsync에") still ends the word.
    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9_]+\b", RegexOptions.ECMAScript)]
    private static partial Regex PascalCase();
}
