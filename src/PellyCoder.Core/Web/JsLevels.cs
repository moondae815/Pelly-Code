using Acornima;

namespace PellyCoder.Core.Web;

/// <summary>A JavaScript syntax error; <paramref name="Column"/> is 1-based.</summary>
public sealed record JsParseError(int Line, int Column, string Description);

/// <summary>Result of <see cref="JsLevels.Detect"/>: lowest ladder level that parses, or null with the Latest error.</summary>
public sealed record JsLevelResult(EcmaVersion? Level, bool Module, JsParseError? LatestError);

/// <summary>The ECMAScript level ladder ES5, ES2015 … ES2026 plus Latest.</summary>
public static class JsLevels
{
    public static IReadOnlyList<EcmaVersion> Ladder { get; } = new[]
    {
        EcmaVersion.ES5, EcmaVersion.ES2015, EcmaVersion.ES2016, EcmaVersion.ES2017, EcmaVersion.ES2018,
        EcmaVersion.ES2019, EcmaVersion.ES2020, EcmaVersion.ES2021, EcmaVersion.ES2022, EcmaVersion.ES2023,
        EcmaVersion.ES2024, EcmaVersion.ES2025, EcmaVersion.ES2026,
    };

    /// <summary>
    /// Rank of a version; aliases (ES6/ES2015, ES3/ES5) share a rank. In Acornima 1.9.0 Latest has the same
    /// numeric value as ES2026, so the two cannot be told apart and rank equal (and highest).
    /// </summary>
    private static int Rank(EcmaVersion v)
    {
        var n = (int)v;
        return n <= 5 ? 0 : n - 5;
    }

    public static int Compare(EcmaVersion a, EcmaVersion b) => Rank(a).CompareTo(Rank(b));

    public static string Name(EcmaVersion v)
    {
        var r = Rank(v);
        return r == 0 ? "ES5" : "ES" + (r + 2014);
    }

    /// <summary>Parses a web.jsTarget value. "auto" (and anything unrecognized) gives null.</summary>
    public static EcmaVersion? FromSetting(string value)
    {
        var s = (value ?? "").Trim().ToLowerInvariant();
        if (s == "latest") return EcmaVersion.Latest;
        foreach (var v in Ladder)
            if (string.Equals(Name(v), s, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    public static JsLevelResult Detect(string textLf)
    {
        // Latest is the same value as the last ladder entry, so the ladder already ends at Latest.
        var cands = Ladder;
        JsParseError? latest = null;
        foreach (var v in cands)
        {
            var err = JsDocument.TryParse(textLf, v, false, out _);
            if (err is null) return new JsLevelResult(v, false, null);
            if (v == cands[^1]) latest = err;
        }

        if (JsDocument.TryParse(textLf, EcmaVersion.Latest, true, out _) is null)
        {
            foreach (var v in cands.Skip(1))
                if (JsDocument.TryParse(textLf, v, true, out _) is null)
                    return new JsLevelResult(v, true, null);
        }
        return new JsLevelResult(null, false, latest);
    }
}
