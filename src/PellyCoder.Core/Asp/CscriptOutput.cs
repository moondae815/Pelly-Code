using System.Text.RegularExpressions;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Asp;

/// <summary>
/// Compile error lines of <c>cscript</c>: <c>&lt;path&gt;(&lt;line&gt;, &lt;column&gt;) Microsoft VBScript &lt;kind&gt;: &lt;text&gt;</c>.
/// Only the compile kinds (English <c>compilation error</c>, Korean <c>컴파일 오류</c>) count; runtime errors and any other
/// line are ignored.
/// </summary>
public static partial class CscriptOutput
{
    private static readonly HashSet<string> CompileKinds = new(StringComparer.OrdinalIgnoreCase) { "compilation error", "컴파일 오류" };

    /// <param name="scriptPath">The compiled script; it becomes the diagnostics' file (callers map it to the page).</param>
    /// <param name="script">Maps each reported script line to its page line; an error reported past the script's end
    /// (e.g. a missing <c>End</c>) moves to the page's last line. The column stays the script column.</param>
    public static IReadOnlyList<Diagnostic> Parse(string output, string scriptPath, VbsScript script)
    {
        var result = new List<Diagnostic>();
        foreach (var raw in output.Split('\n'))
        {
            var match = ErrorLine().Match(raw.TrimEnd('\r'));
            if (!match.Success || !CompileKinds.Contains(match.Groups["kind"].Value.Trim()))
                continue;
            var line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var column = int.Parse(match.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture);
            result.Add(new Diagnostic(scriptPath, script.PageLine(line), column, "error", "VBS", match.Groups["message"].Value.Trim()));
        }
        return result;
    }

    [GeneratedRegex(@"^(?<path>.+?)\((?<line>\d+),\s*(?<column>\d+)\)\s+Microsoft VBScript\s+(?<kind>[^:]+):\s*(?<message>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLine();
}
