using System.Text;

namespace PellyCoder.Core.Web;

/// <summary>Selector text normalization and matching for CSS rules.</summary>
public static class CssSelector
{
    /// <summary>
    /// Collapses whitespace runs to one space, removes spaces around commas, and puts exactly one space around
    /// the combinators <c>&gt;</c>, <c>+</c> and <c>~</c>. Case is kept. Strings, escapes, brackets and
    /// parentheses are left alone (apart from whitespace collapsing and comma spacing).
    /// </summary>
    public static string Normalize(string selector)
    {
        var sb = new StringBuilder();
        var pendingSpace = false;
        var depth = 0;

        void Flush()
        {
            if (pendingSpace && sb.Length > 0 && sb[^1] != ' ' && sb[^1] != ',') sb.Append(' ');
            pendingSpace = false;
        }

        for (var i = 0; i < selector.Length; i++)
        {
            var c = selector[i];
            if (c == '\\' && i + 1 < selector.Length)
            {
                Flush();
                sb.Append(c).Append(selector[++i]);
            }
            else if (c == '"' || c == '\'')
            {
                Flush();
                sb.Append(c);
                for (i++; i < selector.Length; i++)
                {
                    sb.Append(selector[i]);
                    if (selector[i] == '\\' && i + 1 < selector.Length) sb.Append(selector[++i]);
                    else if (selector[i] == c) break;
                }
            }
            else if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
            }
            else if (c == ',')
            {
                while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                sb.Append(',');
                pendingSpace = false;
            }
            else if (depth == 0 && (c == '>' || c == '+' || c == '~'))
            {
                while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                if (sb.Length > 0 && sb[^1] != ',') sb.Append(' ');
                sb.Append(c).Append(' ');
                pendingSpace = false;
            }
            else
            {
                Flush();
                if (c == '(' || c == '[') depth++;
                else if ((c == ')' || c == ']') && depth > 0) depth--;
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Splits a selector list at top-level commas (not inside strings, brackets or parentheses) and normalizes each part.</summary>
    public static IReadOnlyList<string> Split(string selectorList)
    {
        var parts = new List<string>();
        var normalized = Normalize(selectorList);
        var start = 0;
        var depth = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '\\') i++;
            else if (c == '"' || c == '\'')
            {
                for (i++; i < normalized.Length && normalized[i] != c; i++)
                    if (normalized[i] == '\\') i++;
            }
            else if (c == '(' || c == '[') depth++;
            else if ((c == ')' || c == ']') && depth > 0) depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(normalized[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(normalized[start..].Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }

    /// <summary>True when the normalized query equals one of the rule's selectors.</summary>
    public static bool Matches(CssRule rule, string query)
    {
        var q = Normalize(query);
        return q.Length > 0 && rule.Selectors.Any(s => string.Equals(s, q, StringComparison.Ordinal));
    }
}
