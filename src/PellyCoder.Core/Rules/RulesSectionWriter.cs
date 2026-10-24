using System.Globalization;
using System.Text;

namespace PellyCoder.Core.Rules;

/// <summary>Builds the drafted <c>## Pelly rules</c> section and merges it into AGENTS.md text (LF line breaks only).</summary>
public static class RulesSectionWriter
{
    public const string NewFileTitle = "# AGENTS.md\n\n";

    public static string BuildSection(IReadOnlyList<DraftRule> rules, DateOnly today)
    {
        var sb = new StringBuilder();
        sb.Append("## Pelly rules\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<!-- pelly rules init 초안 ({today:yyyy-MM-dd}). 검토해서 고치거나 지우세요. 형식: - [태그,...] 규칙(100자 이내) [| forbid: 정규식] -->\n");
        if (rules.Count == 0)
            sb.Append("<!-- 일관된 패턴을 찾지 못했습니다. 예: - [*] 주석은 한국어로 작성한다 -->\n");
        foreach (var rule in rules)
        {
            sb.Append(rule.Line).Append('\n');
            sb.Append("<!-- 근거: ").Append(rule.Evidence.Replace("-->", "--&gt;", StringComparison.Ordinal)).Append(" -->\n");
        }
        return sb.ToString();
    }

    /// <summary>No file: a titled new file. Otherwise the section is replaced, or appended when missing.</summary>
    public static string Apply(string? existingFileText, string sectionLf) =>
        existingFileText is null
            ? NewFileTitle + sectionLf.TrimEnd('\n') + "\n"
            : RulesDocument.ReplaceSection(existingFileText, sectionLf);
}
