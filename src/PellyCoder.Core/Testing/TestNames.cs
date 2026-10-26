using System.Text.RegularExpressions;

namespace PellyCoder.Core.Testing;

/// <summary>Test name rules: the shape of a name a spec or flag may carry, and how it is matched against result names.</summary>
public static partial class TestNames
{
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z")]
    private static partial Regex ValidName();

    public static bool IsValid(string name) => ValidName().IsMatch(name);

    /// <summary>Cuts the data-row argument part from the first <c>(</c> and trims surrounding whitespace.</summary>
    public static string WithoutArguments(string fullName)
    {
        var open = fullName.IndexOf('(');
        return (open < 0 ? fullName : fullName[..open]).Trim();
    }

    /// <summary>True when the result name (arguments cut) equals the spec name or ends with a dot plus the spec name; ordinal.</summary>
    public static bool Matches(string specName, string resultFullName)
    {
        var result = WithoutArguments(resultFullName);
        return result == specName || result.EndsWith("." + specName, StringComparison.Ordinal);
    }
}
