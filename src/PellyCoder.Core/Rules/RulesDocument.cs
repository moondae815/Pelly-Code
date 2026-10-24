using System.Text.RegularExpressions;

namespace PellyCoder.Core.Rules;

/// <summary>Finds, parses and rewrites the <c>## Pelly rules</c> section of an AGENTS.md text (LF line breaks).</summary>
public static partial class RulesDocument
{
    public const int MaxTextLength = 100;
    public const int MaxRules = 50;

    [GeneratedRegex(@"^##[ \t]+Pelly rules[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartHeading();

    [GeneratedRegex(@"^#{1,2}(?:[ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex AnyTopHeading();

    [GeneratedRegex(@"^- \[(?<tags>[^\]]+)\][ \t]+(?<text>.+?)(?:[ \t]+\|[ \t]+forbid:[ \t]+(?<forbid>.+?))?[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^[A-Za-z0-9_.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TagName();

    private static List<string> Lines(string text, out bool endsWithNewline)
    {
        if (text.Length == 0) { endsWithNewline = false; return []; }
        var lines = text.Split('\n').ToList();
        endsWithNewline = text.EndsWith('\n');
        if (endsWithNewline) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static (int Start, int End)? Locate(List<string> lines)
    {
        var start = lines.FindIndex(l => StartHeading().IsMatch(l));
        if (start < 0) return null;
        var end = lines.Count;
        for (var i = start + 1; i < lines.Count; i++)
        {
            if (AnyTopHeading().IsMatch(lines[i])) { end = i; break; }
        }
        return (start, end); // [start, end) zero-based
    }

    public static RulesSection? FindSection(string textLf)
    {
        var range = Locate(Lines(textLf, out _));
        return range is { } r ? new RulesSection(r.Start + 1, r.End) : null;
    }

    public static (IReadOnlyList<ProjectRule> Rules, IReadOnlyList<RulesError> Errors) Parse(string textLf)
    {
        var lines = Lines(textLf, out _);
        var rules = new List<ProjectRule>();
        var errors = new List<RulesError>();
        if (Locate(lines) is not { } range) return (rules, errors);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tooMany = false;
        for (var i = range.Start + 1; i < range.End; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("- ") && !line.StartsWith("* ")) continue;
            var no = i + 1;
            var m = RuleLine().Match(line);
            if (!m.Success)
            {
                errors.Add(new RulesError(no, "규칙 줄 형식이 아닙니다. '- [태그] 규칙 | forbid: 정규식' 형태여야 합니다."));
                continue;
            }

            var tags = m.Groups["tags"].Value.Split(',').Select(t => t.Trim()).ToList();
            if (!(tags is ["*"] || tags.All(t => TagName().IsMatch(t))))
            {
                errors.Add(new RulesError(no, "태그는 '*' 단독이거나 영문·숫자·_·. 로 된 이름의 쉼표 목록이어야 합니다."));
                continue;
            }

            var text = m.Groups["text"].Value.Trim();
            if (text.Length == 0 || text.StartsWith('|'))
            {
                errors.Add(new RulesError(no, "규칙 텍스트가 비어 있습니다."));
                continue;
            }

            if (text.Length > MaxTextLength)
            {
                errors.Add(new RulesError(no, $"규칙 텍스트는 {MaxTextLength}자 이내여야 합니다 (현재 {text.Length}자)."));
                continue;
            }

            string? forbid = m.Groups["forbid"].Success ? m.Groups["forbid"].Value : null;
            if (forbid is not null)
            {
                if (forbid.Trim().Length == 0)
                {
                    errors.Add(new RulesError(no, "forbid 정규식이 비어 있습니다."));
                    continue;
                }
                try { _ = new Regex(forbid, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException ex)
                {
                    errors.Add(new RulesError(no, $"forbid 정규식이 올바르지 않습니다: {ex.Message}"));
                    continue;
                }
            }

            if (!seen.Add(text))
            {
                errors.Add(new RulesError(no, "같은 규칙 텍스트가 이미 있습니다."));
                continue;
            }

            if (rules.Count >= MaxRules)
            {
                if (!tooMany) errors.Add(new RulesError(no, $"규칙은 최대 {MaxRules}개까지입니다."));
                tooMany = true;
                continue;
            }

            rules.Add(new ProjectRule(no, tags, text, forbid));
        }
        return (rules, errors);
    }

    /// <summary>Replaces the section (blank lines that end it are kept); appends after one blank line when absent.</summary>
    public static string ReplaceSection(string textLf, string newSectionLf)
    {
        var lines = Lines(textLf, out var endsWithNewline);
        var added = newSectionLf.TrimEnd('\n').Split('\n');
        var result = new List<string>();
        if (Locate(lines) is { } r)
        {
            var trailingBlank = 0;
            while (r.End - 1 - trailingBlank > r.Start && lines[r.End - 1 - trailingBlank].Trim().Length == 0) trailingBlank++;
            result.AddRange(lines.Take(r.Start));
            result.AddRange(added);
            result.AddRange(lines.Skip(r.End - trailingBlank).Take(trailingBlank));
            result.AddRange(lines.Skip(r.End));
            return string.Join('\n', result) + (endsWithNewline || r.End == lines.Count ? "\n" : "");
        }

        result.AddRange(lines);
        if (result.Count > 0) result.Add("");
        result.AddRange(added);
        return string.Join('\n', result) + "\n";
    }
}
