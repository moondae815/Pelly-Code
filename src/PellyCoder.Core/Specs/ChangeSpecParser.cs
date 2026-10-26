using System.Text.RegularExpressions;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Specs;

/// <summary>Parses a <c>changes/&lt;id&gt;.md</c> spec with a fixed grammar. No model involved.</summary>
public static class ChangeSpecParser
{
    public const int MaxTasks = 20;

    private static readonly Regex TaskLine = new(
        @"^- \[( |x)\]\s+(T\d+)\s+(cs|sql)\s+(\S+?)(?:#(\S+))?\s+(.+?)(?:\s+\((AC\d+(?:\s*,\s*AC\d+)*)\))?(?:\s+\[(G\d+)\][.,;:]?)?(?:\s*<!--\s*(run|failed):\s*([^>]*?)\s*-->)?\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex TaskShaped = new(
        @"^\s*(?:[-*+]|\d+[.)])\s*\[.?\]|\bT\d+\s+(?:cs|sql)\s",
        RegexOptions.CultureInvariant);

    // A checkbox list item that starts with a task ID and kind: a task line wherever it stands.
    private static readonly Regex TaskCheckbox = new(
        @"^\s*(?:[-*+]|\d+[.)])\s*\[.?\]\s*T\d+\s+(?:cs|sql)\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex CriterionLine = new(@"^- (AC\d+):\s*(.+?)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex SubLine = new(@"^\s+(test|breaks):\s*(.+?)\s*$", RegexOptions.CultureInvariant);

    public static ChangeSpec Parse(string textLf)
    {
        var lines = textLf.Split('\n');
        var issues = new List<SpecIssue>();
        var criteria = new List<AcceptanceCriterion>();
        var tasks = new List<SpecTask>();

        var (id, status, bodyStart) = ParseFrontMatter(lines, issues);

        string? title = null;
        var section = Section.None;
        var tasksHeadingLine = 0;
        for (var i = bodyStart; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNo = i + 1;

            if (title is null && line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line[2..].Trim();
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var name = line[3..].Trim();
                section = name == "수용 기준" ? Section.Criteria : name == "작업" ? Section.Tasks : Section.Other;
                if (section == Section.Tasks && tasksHeadingLine == 0) tasksHeadingLine = lineNo;
                continue;
            }

            if (section == Section.Criteria)
            {
                var m = CriterionLine.Match(line);
                if (m.Success)
                {
                    var acId = m.Groups[1].Value;
                    string? test = null;
                    var breaks = new List<(string Name, int Line)>();
                    // Indented test:/breaks: lines directly after the AC line, in any order; any other line ends them.
                    while (i + 1 < lines.Length && SubLine.Match(lines[i + 1]) is { Success: true } sub)
                    {
                        i++;
                        var subNo = i + 1;
                        var value = sub.Groups[2].Value;
                        var isTest = sub.Groups[1].Value == "test";
                        if (isTest && test is not null)
                        {
                            issues.Add(Error(subNo, "DuplicateTest", $"수용 기준 {acId}에 test:가 둘 이상입니다."));
                            continue;
                        }
                        if (!TestNames.IsValid(value))
                        {
                            issues.Add(Error(subNo, "InvalidTestName", $"수용 기준 {acId}의 테스트 이름 형식이 아닙니다: {value}"));
                            if (isTest) test = value;
                            continue;
                        }
                        if (isTest) test = value;
                        else if (!breaks.Exists(b => b.Name == value)) breaks.Add((value, subNo));
                    }
                    foreach (var (name, breakLine) in breaks)
                        if (name == test)
                            issues.Add(Error(breakLine, "BreaksSameAsTest",
                                $"수용 기준 {acId}: 같은 테스트를 test:와 breaks:에 함께 둘 수 없습니다: {name}"));
                    criteria.Add(new AcceptanceCriterion(acId, m.Groups[2].Value, test,
                        breaks.Count > 0 ? breaks.Select(b => b.Name).ToArray() : null));
                }
                else if (TaskCheckbox.IsMatch(line))
                {
                    issues.Add(Error(lineNo, "InvalidTaskLine", OutsideTasksMessage));
                }
            }
            else if (section == Section.Tasks)
            {
                var m = TaskLine.Match(line);
                if (m.Success)
                    tasks.Add(ToTask(m, lineNo));
                else if (TaskShaped.IsMatch(line))
                    issues.Add(Error(lineNo, "InvalidTaskLine",
                        "작업 줄이 문법에 맞지 않습니다: - [ ] <ID> <cs|sql> <파일>[#심볼] <요청> [(AC 참조)] [[G<n>]]"));
            }
            else if (TaskCheckbox.IsMatch(line))
            {
                // Never silently skipped: apply would finish and archive the spec without running it.
                issues.Add(Error(lineNo, "InvalidTaskLine", OutsideTasksMessage));
            }
        }

        Validate(criteria, tasks, issues, tasksHeadingLine);
        return new ChangeSpec(id, status, title, criteria, tasks, issues);
    }

    private const string OutsideTasksMessage =
        "작업 줄이 '## 작업' 절 밖에 있습니다. 작업은 '## 작업' 제목 아래에만 둘 수 있습니다.";

    private enum Section { None, Criteria, Tasks, Other }

    private static SpecIssue Error(int line, string code, string message) =>
        new(line, SpecIssueSeverity.Error, code, message);

    private static (string? Id, SpecStatus Status, int BodyStart) ParseFrontMatter(string[] lines, List<SpecIssue> issues)
    {
        var end = -1;
        if (lines.Length > 0 && lines[0].TrimEnd() == "---")
            for (var i = 1; i < lines.Length; i++)
                if (lines[i].TrimEnd() == "---") { end = i; break; }

        if (end < 0)
        {
            issues.Add(Error(1, "FrontMatter", "머리말(---)이 없습니다. id와 status가 필요합니다."));
            return (null, SpecStatus.Draft, 0);
        }

        string? id = null;
        SpecStatus? status = null;
        string? rawStatus = null;
        for (var i = 1; i < end; i++)
        {
            var (key, value) = SplitKeyValue(lines[i]);
            if (key == "id") id = value;
            else if (key == "status") rawStatus = value;
        }

        if (string.IsNullOrEmpty(id))
        {
            id = null;
            issues.Add(Error(1, "FrontMatter", "머리말에 id가 없습니다."));
        }

        status = rawStatus switch
        {
            "draft" => SpecStatus.Draft,
            "in-progress" => SpecStatus.InProgress,
            "done" => SpecStatus.Done,
            _ => null,
        };
        if (status is null)
            issues.Add(Error(1, "FrontMatter", "머리말의 status는 draft | in-progress | done 중 하나여야 합니다."));

        return (id, status ?? SpecStatus.Draft, end + 1);
    }

    private static (string Key, string Value) SplitKeyValue(string line)
    {
        var (key, value) = FrontMatterField(line);
        return (key, line[value]);
    }

    /// <summary>
    /// The one key/value rule for a front-matter line: the key is the trimmed text before the first colon, the value
    /// the trimmed text after it up to a <c>#</c> comment. Returns the value's range so a writer can replace it in place.
    /// </summary>
    public static (string Key, Range Value) FrontMatterField(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return ("", 0..0);
        var end = line.IndexOf('#', colon + 1);
        if (end < 0) end = line.Length;
        var start = colon + 1;
        while (start < end && char.IsWhiteSpace(line[start])) start++;
        while (end > start && char.IsWhiteSpace(line[end - 1])) end--;
        return (line[..colon].Trim(), start..end);
    }

    private static SpecTask ToTask(Match m, int lineNo)
    {
        var acRefs = m.Groups[7].Success
            ? m.Groups[7].Value.Split(',').Select(s => s.Trim()).ToArray()
            : Array.Empty<string>();

        string? runId = null, failure = null;
        if (m.Groups[9].Success)
        {
            var value = m.Groups[10].Value;
            if (m.Groups[9].Value == "run")
                runId = value.Length > 0 ? value : null;
            else
            {
                var parts = value.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                runId = parts.Length > 0 ? parts[0] : null;
                failure = parts.Length > 1 ? parts[1].Trim() : null;
            }
        }

        return new SpecTask(
            m.Groups[2].Value,
            m.Groups[3].Value == "cs" ? TaskKind.CSharp : TaskKind.Sql,
            m.Groups[4].Value,
            m.Groups[5].Success ? m.Groups[5].Value : null,
            m.Groups[6].Value,
            acRefs,
            m.Groups[8].Success ? m.Groups[8].Value : null,
            m.Groups[1].Value == "x",
            runId,
            failure,
            lineNo);
    }

    private static void Validate(
        List<AcceptanceCriterion> criteria, List<SpecTask> tasks, List<SpecIssue> issues, int tasksHeadingLine)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
            if (!seen.Add(task.Id))
                issues.Add(Error(task.Line, "DuplicateId", $"작업 ID {task.Id}가 중복됩니다."));

        if (tasks.Count > MaxTasks)
            issues.Add(Error(tasks[MaxTasks].Line, "TooManyTasks", $"작업은 최대 {MaxTasks}개입니다(현재 {tasks.Count}개)."));

        var finished = new HashSet<string>(StringComparer.Ordinal);
        string? current = null;
        foreach (var task in tasks)
        {
            if (task.Group != current)
            {
                if (current is not null) finished.Add(current);
                current = task.Group;
                if (current is not null && finished.Contains(current))
                    issues.Add(Error(task.Line, "GroupNotContiguous", $"그룹 {current}의 작업은 연속해야 합니다."));
            }
            if (task.Group is not null && task.Kind == TaskKind.Sql)
                issues.Add(Error(task.Line, "SqlInGroup", $"sql 작업 {task.Id}는 그룹에 넣을 수 없습니다."));
        }

        var known = criteria.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var task in tasks)
            foreach (var ac in task.AcRefs)
                if (!known.Contains(ac))
                    issues.Add(Error(task.Line, "UnknownAc", $"작업 {task.Id}가 없는 수용 기준 {ac}을(를) 참조합니다."));

        if (tasks.Count == 0)
            issues.Add(Error(Math.Max(tasksHeadingLine, 1), "NoTasks", "작업이 하나도 없습니다."));
    }
}
