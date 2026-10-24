using System.Text;
using PellyCoder.Core.Files;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Tests.Rules;

public class RulesSectionWriterTests
{
    private static readonly DraftRule[] Two =
    [
        new("- [async] async 메서드 이름은 Async로 끝낸다", "async 메서드 42개 중 41개가 Async로 끝남"),
        new("- [sql] 쿼리는 매개변수로 작성한다 | forbid: string\\.Format", "SQL 12개 중 12개"),
    ];

    [Fact]
    public void BuildSection_FormatsHeaderRulesAndEvidence()
    {
        var s = RulesSectionWriter.BuildSection(Two, new DateOnly(2026, 10, 24));
        Assert.Equal(
            "## Pelly rules\n" +
            "<!-- pelly rules init 초안 (2026-10-24). 검토해서 고치거나 지우세요. 형식: - [태그,...] 규칙(100자 이내) [| forbid: 정규식] -->\n" +
            "- [async] async 메서드 이름은 Async로 끝낸다\n" +
            "<!-- 근거: async 메서드 42개 중 41개가 Async로 끝남 -->\n" +
            "- [sql] 쿼리는 매개변수로 작성한다 | forbid: string\\.Format\n" +
            "<!-- 근거: SQL 12개 중 12개 -->\n", s);
        var (rules, errors) = RulesDocument.Parse(s);
        Assert.Empty(errors);
        Assert.Equal(2, rules.Count);
    }

    [Fact]
    public void BuildSection_NoRules_ExplainsAndParsesEmpty()
    {
        var s = RulesSectionWriter.BuildSection([], new DateOnly(2026, 1, 2));
        Assert.Contains("<!-- 일관된 패턴을 찾지 못했습니다. 예: - [*] 주석은 한국어로 작성한다 -->\n", s);
        var (rules, errors) = RulesDocument.Parse(s);
        Assert.Empty(rules);
        Assert.Empty(errors);
    }

    [Fact]
    public void Apply_NoFile_CreatesWithTitle()
    {
        var section = RulesSectionWriter.BuildSection(Two, new DateOnly(2026, 10, 24));
        Assert.Equal("# AGENTS.md\n\n" + section, RulesSectionWriter.Apply(null, section));
    }

    [Fact]
    public void Apply_ExistingWithoutSection_AppendsAndKeepsRest()
    {
        var section = RulesSectionWriter.BuildSection(Two, new DateOnly(2026, 10, 24));
        var existing = "# Guide\n\nSome text.\n";
        var result = RulesSectionWriter.Apply(existing, section);
        Assert.StartsWith(existing + "\n## Pelly rules\n", result);
        Assert.NotNull(RulesDocument.FindSection(result));
    }

    [Fact]
    public void Apply_ReplacesOnlySection_PreservesCrlfAndBom()
    {
        var section = RulesSectionWriter.BuildSection(Two, new DateOnly(2026, 10, 24));
        var before = "# Guide\r\n\r\nIntro 한글\r\n\r\n";
        var after = "## Other\r\n\r\nTail\r\n";
        var original = before + "## Pelly rules\r\n- [*] old rule\r\n\r\n" + after;
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new UTF8Encoding(false).GetBytes(original)).ToArray();

        var snap = TextFileCodec.Decode("/w/AGENTS.md", bytes);
        var written = TextFileCodec.Encode(snap, RulesSectionWriter.Apply(snap.Text, section));

        var expected = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new UTF8Encoding(false).GetBytes(
            before + section.Replace("\n", "\r\n") + "\r\n" + after)).ToArray();
        Assert.Equal(expected, written);
    }
}
