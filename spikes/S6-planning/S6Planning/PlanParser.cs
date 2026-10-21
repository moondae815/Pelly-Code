using System.Text.RegularExpressions;

namespace S6Planning;

/// <summary>One V2-1 task line: <c>- [ ] T&lt;n&gt; &lt;cs|sql&gt; &lt;path&gt;[#Member] &lt;request&gt; [G&lt;k&gt;]</c>.</summary>
public sealed record PlanTask(string Id, string Kind, string Path, string? Symbol, string Request, string? Group);

/// <summary>Parsed output. Checkbox lines that break the grammar are invalid; any other non-blank line is extra text.</summary>
public sealed record ParsedPlan(IReadOnlyList<PlanTask> Tasks, IReadOnlyList<string> InvalidLines, int ExtraLines)
{
    public bool Parsed => Tasks.Count > 0 && InvalidLines.Count == 0;
}

public static partial class PlanParser
{
    [GeneratedRegex(@"^- \[ \] (T\d+) (cs|sql) (\S+?)(?:#(\S+))? (.+?)(?: \[(G\d+)\])?\s*$")]
    private static partial Regex TaskLine();

    public static ParsedPlan Parse(string text)
    {
        var tasks = new List<PlanTask>();
        var invalid = new List<string>();
        var extra = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
                continue;
            if (!line.StartsWith("- [", StringComparison.Ordinal))
            {
                extra++;
                continue;
            }

            var m = TaskLine().Match(line);
            if (!m.Success)
            {
                invalid.Add(line);
                continue;
            }

            tasks.Add(new PlanTask(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value,
                m.Groups[4].Success ? m.Groups[4].Value : null, m.Groups[5].Value.Trim(),
                m.Groups[6].Success ? m.Groups[6].Value : null));
        }

        return new ParsedPlan(tasks, invalid, extra);
    }
}
