namespace PellyCoder.Core.Llm;

/// <summary>
/// Finds model control token strings (e.g. <c>&lt;|im_end|&gt;</c>) in text that is about to be placed in a raw-mode prompt,
/// where the server would read them as real control tokens.
/// </summary>
public static class SpecialTokenGuard
{
    /// <summary>The token that occurs first in <paramref name="text"/> (ordinal), or null.</summary>
    public static string? FindIn(string text, IReadOnlyCollection<string> specialTokens)
    {
        string? found = null;
        var best = int.MaxValue;
        foreach (var token in specialTokens)
        {
            var index = text.IndexOf(token, StringComparison.Ordinal);
            if (index >= 0 && index < best)
            {
                best = index;
                found = token;
            }
        }
        return found;
    }
}
