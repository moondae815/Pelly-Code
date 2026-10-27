using System.Globalization;
using System.Text.RegularExpressions;

namespace PellyCoder.Core.Sql;

/// <summary>The first-line comment of a pulled file: where the definition came from and its normalized hash.</summary>
public sealed record PullRecord(string Source, string Server, string Database, SqlObjectName Object, string Sha256, DateTimeOffset At)
{
    private const string Prefix = "-- pelly pull: ";
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly Regex Pattern = new(
        @"^-- pelly pull: source=(?<source>\S+) server=(?<server>.+?) database=(?<db>.+?) object=(?<obj>.+?) sha256=(?<hash>[0-9a-f]+) at=(?<at>\S+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string Format() =>
        $"{Prefix}source={Source} server={Server} database={Database} object={Bracketed(Object)} sha256={Sha256} at={At.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture)}";

    /// <summary>Reads only the first line. Returns null when it is not a pull record.</summary>
    public static PullRecord? TryParse(string fileTextLf)
    {
        ArgumentNullException.ThrowIfNull(fileTextLf);
        var text = fileTextLf.TrimStart('\uFEFF');
        var end = text.IndexOf('\n');
        var line = (end < 0 ? text : text[..end]).TrimEnd('\r');
        var m = Pattern.Match(line);
        if (!m.Success)
            return null;
        if (!DateTimeOffset.TryParseExact(m.Groups["at"].Value, TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            return null;
        SqlObjectName name;
        try { name = SqlObjectName.Parse(m.Groups["obj"].Value); }
        catch (FormatException) { return null; }
        return new PullRecord(m.Groups["source"].Value, m.Groups["server"].Value, m.Groups["db"].Value, name, m.Groups["hash"].Value, at);
    }

    private static string Bracketed(SqlObjectName name) =>
        $"[{(name.Schema ?? "dbo").Replace("]", "]]")}].[{name.Name.Replace("]", "]]")}]";
}

/// <summary>
/// A pull record that pelly rewrites because the file is production already (the user deployed their edit, design 6.2):
/// <see cref="StaleLine"/> is the file's first line as read, <see cref="RefreshedLine"/> the same record with production's
/// hash and the refresh time. Only that line changes.
/// </summary>
public sealed record SqlRecordRefresh(string StaleLine, string RefreshedLine)
{
    /// <summary>
    /// The refresh of <paramref name="stale"/>, the record on the first line of <paramref name="fileTextLf"/> (LF, no BOM);
    /// null when the first line is not that record.
    /// </summary>
    public static SqlRecordRefresh? For(string fileTextLf, PullRecord stale, string prodHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fileTextLf);
        ArgumentNullException.ThrowIfNull(stale);
        ArgumentException.ThrowIfNullOrEmpty(prodHash);
        var line = FirstLine(fileTextLf);
        if (PullRecord.TryParse(line) != stale)
            return null;
        return new SqlRecordRefresh(line, (stale with { Sha256 = prodHash, At = now }).Format());
    }

    /// <summary><paramref name="textLf"/> with its first line replaced when that line is exactly <see cref="StaleLine"/>; otherwise unchanged.</summary>
    public string ApplyTo(string textLf)
    {
        ArgumentNullException.ThrowIfNull(textLf);
        var line = FirstLine(textLf);
        return line == StaleLine ? string.Concat(RefreshedLine, textLf.AsSpan(line.Length)) : textLf;
    }

    /// <summary>True when the first line of <paramref name="textLf"/> is <see cref="RefreshedLine"/>.</summary>
    public bool IsIn(string textLf) => FirstLine(textLf) == RefreshedLine;

    private static string FirstLine(string textLf)
    {
        var end = textLf.IndexOf('\n');
        return end < 0 ? textLf : textLf[..end];
    }
}
