using System.Text.RegularExpressions;

namespace PellyCoder.Core.Specs;

/// <summary>Pulls code-looking names out of a task's free-text request so the checker can verify they exist.</summary>
public static partial class RequestIdentifiers
{
    /// <summary>
    /// Backtick strings verbatim, plus (outside backticks) ASCII PascalCase words of two or more letters, words starting
    /// with <c>_</c>, and <c>schema.Name</c> pairs. Korean words and pure numbers never match. Each word appears once.
    /// </summary>
    public static IReadOnlyList<string> Extract(string request)
    {
        var found = new List<string>();
        foreach (Match m in Backticked().Matches(request))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length > 0) found.Add(text);
        }

        foreach (Match m in Word().Matches(Backticked().Replace(request, " ")))
        {
            var w = m.Value;
            if (w.Contains('.') ? IsSchemaName(w) : IsPascal(w) || IsUnderscore(w))
                found.Add(w);
        }

        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool IsPascal(string w) => w.Length >= 2 && char.IsAsciiLetterUpper(w[0]) && w.Any(char.IsAsciiLetterLower);

    private static bool IsUnderscore(string w) => w.Length >= 2 && w[0] == '_';

    // "schema.Name": exactly two parts, each a real name (keeps "e.g" and "v1.2" out).
    private static bool IsSchemaName(string w)
    {
        var parts = w.Split('.');
        return parts.Length == 2 && parts.All(p => p.Length >= 2 && (char.IsAsciiLetter(p[0]) || p[0] == '_'));
    }

    [GeneratedRegex("`([^`\n]+)`")]
    private static partial Regex Backticked();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}
