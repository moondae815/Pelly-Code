using System.Text.RegularExpressions;
using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Web;

/// <summary>
/// Site-relative path rules for what a page refers to. Includes follow <see cref="AspIncludeResolver"/> (<c>file=</c> from the
/// including file's folder, <c>virtual=</c> from the site root); <c>&lt;script src&gt;</c> follows the browser (a leading '/'
/// is the site root, anything else is the page's folder). All results use '/' and have no <c>.</c> or <c>..</c> segments.
/// </summary>
internal static partial class PagePaths
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*:")]
    private static partial Regex Scheme();

    public static string Directory(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i < 0 ? "" : rel[..i];
    }

    public static string? ResolveInclude(string pageDir, AspInclude include) =>
        Normalize(include.Virtual ? include.Path.Trim().Replace('\\', '/') : Join(pageDir, include.Path.Trim().Replace('\\', '/')));

    /// <summary>The site-relative path of a <c>&lt;script src&gt;</c>; null for external, server-built or empty values.</summary>
    public static string? ResolveScriptSrc(string pageDir, string src)
    {
        var s = src.Trim().Replace('\\', '/');
        if (s.Length == 0 || s.StartsWith("//", StringComparison.Ordinal) || s.Contains("<%", StringComparison.Ordinal) || Scheme().IsMatch(s))
            return null;
        var cut = s.IndexOfAny(['?', '#']);
        if (cut >= 0) s = s[..cut];
        if (s.Length == 0) return null;
        return Normalize(s.StartsWith('/') ? s : Join(pageDir, s));
    }

    private static string Join(string dir, string raw) =>
        raw.StartsWith('/') ? raw : dir.Length == 0 ? raw : dir + "/" + raw;

    private static string? Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }
}
