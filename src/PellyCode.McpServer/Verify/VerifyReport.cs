namespace PellyCode.McpServer.Verify;

/// <summary>
/// The <c>pelly_verify</c> text (design §7): the first line 「결과: 새 오류 N건, 새 경고 M건」 (or 「결과: 새 문제 없음」), the notes,
/// then the sections in the order they were first added. A section is a heading such as <c>[빌드] Shop.Api</c>,
/// <c>[테스트] Shop.Api.Tests</c> or <c>[SQL] Sql/objects/fn_OrderTotal.sql</c>. Errors count as errors (build errors, failing tests,
/// SQL compile errors, VBScript syntax errors, undefined ASP names), every other check adds warnings.
/// </summary>
public sealed class VerifyReport
{
    /// <summary>Lines shown per section; the rest becomes 「외 N건」.</summary>
    public const int MaxLines = 20;

    public const string NoProblems = "결과: 새 문제 없음";

    /// <summary>The first line when no changed file could be checked (then 「새 문제 없음」 would claim too much).</summary>
    public const string NothingChecked = "결과: 검사한 파일이 없습니다";

    /// <summary>Changed files listed on the 「검사하지 않은 바뀐 파일」 line; the rest becomes 「외 N개」.</summary>
    public const int MaxUncheckedNames = 10;

    private readonly List<object> _entries = []; // Section or a ready line (pass, skip)
    private readonly Dictionary<string, Section> _sections = new(StringComparer.Ordinal);
    private readonly List<string> _notes = [];

    public int ErrorCount => _sections.Values.Sum(s => s.Errors.Count);
    public int WarningCount => _sections.Values.Sum(s => s.Warnings.Count);

    /// <summary>A new error under <paramref name="section"/>; <paramref name="line"/> is shown as <c>- line</c>.</summary>
    public void Error(string section, string line) => Get(section).Errors.Add(line);

    /// <summary>A new warning under <paramref name="section"/>; <paramref name="line"/> is shown as <c>- line</c>.</summary>
    public void Warning(string section, string line) => Get(section).Warnings.Add(line);

    /// <summary>The line <c>&lt;section&gt; — 통과</c>.</summary>
    public void Pass(string section) => _entries.Add($"{section} — 통과");

    /// <summary>
    /// The line <c>&lt;section&gt; 건너뜀 — &lt;reason&gt;</c> (e.g. <c>[테스트] 건너뜀 — …</c>); a section that already starts with
    /// <c>[건너뜀]</c> gives <c>[건너뜀] &lt;what&gt; — &lt;reason&gt;</c>. A skip never counts as a problem.
    /// </summary>
    public void Skip(string section, string reason) =>
        _entries.Add(section.StartsWith("[건너뜀]", StringComparison.Ordinal) ? $"{section} — {reason}" : $"{section} 건너뜀 — {reason}");

    /// <summary>A line right under the first line (e.g. 「HEAD 기준을 만들지 못해 모든 오류를 보입니다: …」).</summary>
    public void Note(string firstLineNote)
    {
        if (!_notes.Contains(firstLineNote, StringComparer.Ordinal)) _notes.Add(firstLineNote);
    }

    /// <summary>
    /// The line 「[건너뜀] 검사하지 않은 바뀐 파일 N개 — a, b, …」 for changed files no check looked at (a skip: not a problem).
    /// </summary>
    public void Unchecked(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var names = string.Join(", ", paths.Take(MaxUncheckedNames));
        if (paths.Count > MaxUncheckedNames) names += $" 외 {paths.Count - MaxUncheckedNames}개";
        Skip($"[건너뜀] 검사하지 않은 바뀐 파일 {paths.Count}개", names);
    }

    /// <summary>The whole text when none of the changed files could be checked: 「결과: 검사한 파일이 없습니다」 and the skip line.</summary>
    public static string RenderNothingChecked(IReadOnlyList<string> paths)
    {
        var report = new VerifyReport();
        report.Unchecked(paths);
        return string.Join('\n', new[] { NothingChecked }.Concat(report._entries.Cast<string>()));
    }

    public string Render()
    {
        var errors = ErrorCount;
        var warnings = WarningCount;
        var lines = new List<string> { errors == 0 && warnings == 0 ? NoProblems : $"결과: 새 오류 {errors}건, 새 경고 {warnings}건" };
        lines.AddRange(_notes);
        foreach (var entry in _entries)
        {
            if (entry is not Section s)
            {
                lines.Add((string)entry);
                continue;
            }
            lines.Add($"{s.Name} — {s.Summary()}");
            var all = s.Errors.Concat(s.Warnings).ToList();
            lines.AddRange(all.Take(MaxLines).Select(l => "- " + l));
            if (all.Count > MaxLines) lines.Add($"외 {all.Count - MaxLines}건");
        }
        return string.Join('\n', lines);
    }

    private Section Get(string name)
    {
        if (!_sections.TryGetValue(name, out var s))
        {
            _sections[name] = s = new Section(name);
            _entries.Add(s);
        }
        return s;
    }

    private sealed class Section(string name)
    {
        public string Name { get; } = name;
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];

        /// <summary>「새 오류 N건」, 「새 경고 M건」 or both; a test section's errors read 「새로 실패 N건」.</summary>
        public string Summary()
        {
            var parts = new List<string>();
            if (Errors.Count > 0)
                parts.Add(Name.StartsWith("[테스트]", StringComparison.Ordinal) ? $"새로 실패 {Errors.Count}건" : $"새 오류 {Errors.Count}건");
            if (Warnings.Count > 0) parts.Add($"새 경고 {Warnings.Count}건");
            return string.Join(", ", parts);
        }
    }
}
