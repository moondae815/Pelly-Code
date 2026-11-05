using System.Text;
using System.Text.RegularExpressions;

namespace PellyCoder.Core.Web;

/// <summary>Small glob matcher for web.vendorPaths: '/' separated, case-insensitive, ** / * / ?.</summary>
public static class WebGlob
{
    public static bool IsMatch(string pattern, string relativePath)
    {
        var p = pattern.Replace('\\', '/');
        var path = relativePath.Replace('\\', '/');
        var sb = new StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                i++;
                if (i + 1 < p.Length && p[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                else sb.Append(".*");
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return Regex.IsMatch(path, sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
