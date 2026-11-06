using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;

namespace PellyCoder.Core.Web;

public sealed record JqueryFound(Version Version, string RelativePath);

/// <summary>Detects jQuery versions in a site from file headers or file names.</summary>
public static class JqueryVersions
{
    private const int HeadBytes = 4096;
    private static readonly Regex Header = new(@"jQuery (?:JavaScript Library )?v(\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
    private static readonly Regex NameVersion = new(@"^jquery-(\d+\.\d+(?:\.\d+)?)(?:\.min)?\.js$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<JqueryFound> Find(IFileSystem fs, string siteRoot, int maxFiles = 5000)
    {
        var result = new List<JqueryFound>();
        if (!fs.Directory.Exists(siteRoot)) return result;
        var seen = 0;
        foreach (var file in fs.Directory.EnumerateFiles(siteRoot, "*", SearchOption.AllDirectories))
        {
            if (++seen > maxFiles) break;
            var name = fs.Path.GetFileName(file);
            if (!name.StartsWith("jquery", StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) continue;

            if (VersionOf(fs, file) is not { } version) continue;

            var rel = fs.Path.GetRelativePath(siteRoot, file).Replace('\\', '/');
            result.Add(new JqueryFound(version, rel));
        }
        return result;
    }

    /// <summary>The version of one jQuery file: from its header, else from a <c>jquery-1.8.3(.min).js</c> file name; null when neither says.</summary>
    public static Version? VersionOf(IFileSystem fs, string file)
    {
        var name = fs.Path.GetFileName(file);
        Version? version = null;
        try
        {
            using var s = fs.File.OpenRead(file);
            var buf = new byte[HeadBytes];
            var n = s.Read(buf, 0, buf.Length);
            var m = Header.Match(Encoding.Latin1.GetString(buf, 0, n));
            if (m.Success) Version.TryParse(m.Groups[1].Value, out version);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (version is null)
        {
            var m = NameVersion.Match(name);
            if (m.Success) Version.TryParse(m.Groups[1].Value, out version);
        }
        return version;
    }

    /// <summary>The configured version if it parses, otherwise the lowest found version.</summary>
    public static Version? Baseline(IReadOnlyList<JqueryFound> found, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Version.TryParse(configured.Trim(), out var c)) return c;
        return found.Count == 0 ? null : found.Min(f => f.Version);
    }
}
