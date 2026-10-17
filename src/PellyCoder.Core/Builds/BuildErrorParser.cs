using System.Text.RegularExpressions;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Builds;

/// <summary>Turns dotnet build / MSBuild output into diagnostics (errors and warnings), relative to the workspace root.</summary>
public static partial class BuildErrorParser
{
    // 설계 §8.9 정규식. 줄 단위로 적용한다.
    [GeneratedRegex(@"^\s*(?<file>.+?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Z]{2,}\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$")]
    private static partial Regex WithFile();

    // 파일 없는 줄(MSBUILD : error MSB1009: ..., CSC : error CS2001: ...). 위 정규식이 실패했을 때만 쓴다.
    [GeneratedRegex(@"^\s*(?<tool>[^:(]+?)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Z]{2,}\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$")]
    private static partial Regex Fileless();

    public static IReadOnlyList<Diagnostic> Parse(string output, string workspaceRoot, bool caseInsensitivePaths) =>
        Parse(output, [workspaceRoot], caseInsensitivePaths);

    /// <summary>
    /// Like <see cref="Parse(string, string, bool)"/>, with several spellings of the same root (e.g. as given and with
    /// links resolved); a path is made relative to the first root it is under.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Parse(string output, IReadOnlyList<string> workspaceRoots, bool caseInsensitivePaths)
    {
        var comparison = caseInsensitivePaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var roots = workspaceRoots.Select(r => Normalize(r).TrimEnd('/')).Distinct().ToList();
        var result = new List<Diagnostic>();
        var seen = new HashSet<(string, int, int, string, string)>(
            caseInsensitivePaths ? KeyComparer.IgnoreCase : KeyComparer.Ordinal);

        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            var m = WithFile().Match(text);
            string file;
            int ln = 0, col = 0;
            if (m.Success)
            {
                file = Relativize(Normalize(m.Groups["file"].Value.Trim()), roots, comparison);
                ln = int.Parse(m.Groups["line"].Value);
                col = int.Parse(m.Groups["col"].Value);
            }
            else
            {
                m = Fileless().Match(text);
                if (!m.Success) continue;
                file = "";
            }

            var d = new Diagnostic(file, ln, col, m.Groups["sev"].Value, m.Groups["code"].Value, m.Groups["msg"].Value);
            if (seen.Add((d.File, d.Line, d.Column, d.Code, d.Message))) result.Add(d);
        }

        return result;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string Relativize(string file, IReadOnlyList<string> roots, StringComparison comparison)
    {
        foreach (var root in roots)
        {
            if (root.Length > 0 && file.Length > root.Length && file[root.Length] == '/' && file.StartsWith(root, comparison))
                return file[(root.Length + 1)..];
        }
        return file;
    }

    // 경로 칸만 대소문자 옵션을 따르고 나머지는 항상 대소문자를 구분한다.
    private sealed class KeyComparer(StringComparison pathComparison) : IEqualityComparer<(string, int, int, string, string)>
    {
        public static readonly KeyComparer IgnoreCase = new(StringComparison.OrdinalIgnoreCase);
        public static readonly KeyComparer Ordinal = new(StringComparison.Ordinal);

        public bool Equals((string, int, int, string, string) a, (string, int, int, string, string) b) =>
            string.Equals(a.Item1, b.Item1, pathComparison) && a.Item2 == b.Item2 && a.Item3 == b.Item3
            && a.Item4 == b.Item4 && a.Item5 == b.Item5;

        public int GetHashCode((string, int, int, string, string) k) =>
            HashCode.Combine(StringComparer.FromComparison(pathComparison).GetHashCode(k.Item1), k.Item2, k.Item3, k.Item4, k.Item5);
    }
}
