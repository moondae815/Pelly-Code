using System.Text.RegularExpressions;

namespace PellyCoder.Core.Web;

public enum WebFileVerdict { Ok, Minified, Vendor }

/// <summary>Refuses minified files and vendor libraries (by name, path rules or license header).</summary>
public sealed class WebFileFilter
{
    private const int HeaderLines = 40;
    private static readonly Regex MinName = new(@"\.min\.(js|css)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _root;
    private readonly IReadOnlyList<string> _vendorPaths;

    public WebFileFilter(string siteRoot, IReadOnlyList<string> vendorPaths)
    {
        _root = siteRoot.Replace('\\', '/').TrimEnd('/');
        _vendorPaths = vendorPaths;
    }

    public (WebFileVerdict Verdict, string? Reason) Classify(string fullPath, string textLf)
    {
        var path = fullPath.Replace('\\', '/');
        if (MinName.IsMatch(path))
            return (WebFileVerdict.Minified, $"압축된 파일은 고치지 않습니다({(path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "*.min.css" : "*.min.js")})");
        if (LooksMinified(textLf))
            return (WebFileVerdict.Minified, "압축된 파일은 고치지 않습니다(줄이 매우 깁니다)");

        var rel = Relative(path);
        if (rel is not null)
        {
            foreach (var pattern in _vendorPaths)
                if (WebGlob.IsMatch(pattern, rel))
                    return (WebFileVerdict.Vendor, $"라이브러리 파일은 고치지 않습니다({pattern} 경로). web.vendorPaths에서 빼면 고칠 수 있습니다");
        }

        if (HasLibraryHeader(textLf))
            return (WebFileVerdict.Vendor, "라이브러리 파일은 고치지 않습니다(머리 주석에 라이선스 표기). web.vendorPaths와 무관하게 고치지 않습니다");
        return (WebFileVerdict.Ok, null);
    }

    private string? Relative(string path)
    {
        if (path.Length <= _root.Length + 1) return null;
        if (!path.StartsWith(_root + "/", StringComparison.OrdinalIgnoreCase)) return null;
        return path[(_root.Length + 1)..];
    }

    private static bool LooksMinified(string text)
    {
        var lines = text.Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0) count--;
        if (count == 0) return false;
        long total = 0;
        var longLines = 0;
        for (var i = 0; i < count; i++)
        {
            total += lines[i].Length;
            if (lines[i].Length > 1000) longLines++;
        }
        return (double)total / count > 500 || longLines * 2 >= count;
    }

    private static bool HasLibraryHeader(string text)
    {
        var lines = text.Split('\n').Take(HeaderLines).ToArray();
        var head = string.Join("\n", lines);
        var start = head.IndexOf("/*", StringComparison.Ordinal);
        if (start < 0) return false;
        var end = head.IndexOf("*/", start + 2, StringComparison.Ordinal);
        var comment = end < 0 ? head[start..] : head[start..(end + 2)];
        if (comment.Contains("jQuery JavaScript Library", StringComparison.OrdinalIgnoreCase)) return true;
        if (comment.Contains("@license", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var line in comment.Split('\n'))
            if (line.Contains("(c)", StringComparison.OrdinalIgnoreCase)
                && line.Contains("license", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
