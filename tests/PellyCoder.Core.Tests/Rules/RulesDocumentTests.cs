using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Tests.Rules;

public class RulesDocumentTests
{
    [Fact]
    public void Parse_ReadsTagsTextAndForbid()
    {
        var (rules, errors) = RulesDocument.Parse("# A\n## Pelly rules\n- [async] .Result 금지 | forbid: \\.Result\\b\n");
        Assert.Empty(errors);
        var r = Assert.Single(rules);
        Assert.Equal(3, r.Line);
        Assert.Equal(["async"], r.Tags);
        Assert.Equal(".Result 금지", r.Text);
        Assert.Equal(@"\.Result\b", r.Forbid);
        Assert.False(r.IsAlways);
    }

    [Fact]
    public void Parse_AlwaysTag()
    {
        var (rules, errors) = RulesDocument.Parse("## Pelly rules\n- [*] 주석은 한국어\n- [*,async] x\n");
        Assert.True(Assert.Single(rules).IsAlways);
        Assert.Null(rules[0].Forbid);
        var e = Assert.Single(errors);
        Assert.Equal(3, e.Line);
    }

    [Fact]
    public void Parse_TagsTrimmedAndValidated()
    {
        var (rules, errors) = RulesDocument.Parse("## Pelly rules\n- [ a , b.c_1 ] x\n- [a b] y\n- [a,,b] z\n");
        Assert.Equal(["a", "b.c_1"], Assert.Single(rules).Tags);
        Assert.Equal([3, 4], errors.Select(e => e.Line).ToArray());
    }

    [Fact]
    public void Parse_SectionEndsAtNextLevel2Heading_KeepsLevel3Inside()
    {
        var (rules, errors) = RulesDocument.Parse("## Pelly rules\n### C#\n- [a] x\n## Other\n- [b] y\n");
        Assert.Empty(errors);
        Assert.Equal("x", Assert.Single(rules).Text);
    }

    [Fact]
    public void Parse_SectionEndsAtLevel1Heading()
    {
        var (rules, _) = RulesDocument.Parse("## Pelly rules\n- [a] x\n# Top\n- [b] y\n");
        Assert.Equal("x", Assert.Single(rules).Text);
    }

    [Fact]
    public void Parse_IgnoresProseAndComments()
    {
        var (rules, errors) = RulesDocument.Parse("## Pelly rules\n이 절은 규칙입니다.\n\n<!-- 근거: 사고 -->\n- [a] x\n");
        Assert.Empty(errors);
        Assert.Single(rules);
    }

    [Fact]
    public void Parse_ErrorOnMalformedListItem()
    {
        var (rules, errors) = RulesDocument.Parse("## Pelly rules\n- [async .Result\n* 그냥 문장\n");
        Assert.Empty(rules);
        Assert.Equal([2, 3], errors.Select(e => e.Line).ToArray());
    }

    [Fact]
    public void Parse_ErrorOnTooLongText()
    {
        var (_, ok) = RulesDocument.Parse($"## Pelly rules\n- [a] {new string('가', 100)}\n");
        Assert.Empty(ok);
        var (rules, errors) = RulesDocument.Parse($"## Pelly rules\n- [a] {new string('가', 101)}\n");
        Assert.Empty(rules);
        Assert.Equal(2, Assert.Single(errors).Line);
    }

    [Fact]
    public void Parse_ErrorOnDuplicateText()
    {
        var (rules, errors) = RulesDocument.Parse("## Pelly rules\n- [a] same\n- [b] same \n");
        Assert.Single(rules);
        Assert.Equal(3, Assert.Single(errors).Line);
    }

    [Fact]
    public void Parse_ErrorOnBadRegex()
    {
        var (rules, errors) = RulesDocument.Parse("## Pelly rules\n- [a] x | forbid: (\n");
        Assert.Empty(rules);
        Assert.Equal(2, Assert.Single(errors).Line);
    }

    [Fact]
    public void Parse_ErrorOnMoreThan50Rules()
    {
        var text = "## Pelly rules\n" + string.Concat(Enumerable.Range(1, 51).Select(i => $"- [a] rule {i}\n"));
        var (rules, errors) = RulesDocument.Parse(text);
        Assert.Equal(50, rules.Count);
        Assert.Equal(52, Assert.Single(errors).Line);
    }

    [Fact]
    public void Parse_NoSection_Empty()
    {
        var (rules, errors) = RulesDocument.Parse("# A\n- [a] x\n");
        Assert.Empty(rules);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("## Pelly Rules  \n- [a] x\n", true)]
    [InlineData("##Pelly rules\n", false)]
    [InlineData("### Pelly rules\n", false)]
    public void FindSection_HeadingVariants(string text, bool found)
    {
        var s = RulesDocument.FindSection(text);
        Assert.Equal(found, s is not null);
        if (found) Assert.Equal(new RulesSection(1, 2), s);
    }

    [Fact]
    public void FindSection_EndsBeforeNextHeading()
    {
        Assert.Equal(new RulesSection(2, 3), RulesDocument.FindSection("# A\n## Pelly rules\n- [a] x\n## B\nz\n"));
    }

    [Fact]
    public void ReplaceSection_AppendsWhenMissing()
    {
        Assert.Equal("# A\ntext\n\n## Pelly rules\n- [a] x\n", RulesDocument.ReplaceSection("# A\ntext\n", "## Pelly rules\n- [a] x\n"));
        Assert.Equal("## Pelly rules\n- [a] x\n", RulesDocument.ReplaceSection("", "## Pelly rules\n- [a] x\n"));
    }

    [Fact]
    public void ReplaceSection_ReplacesOnlySection()
    {
        var before = "# A\nintro\n\n## Pelly rules\n- [a] old\n\n## Other\ntail\n";
        var after = RulesDocument.ReplaceSection(before, "## Pelly rules\n- [b] new\n- [c] more\n");
        Assert.Equal("# A\nintro\n\n## Pelly rules\n- [b] new\n- [c] more\n\n## Other\ntail\n", after);
    }

    [Fact]
    public void ReplaceSection_SectionAtEndOfFile()
    {
        var after = RulesDocument.ReplaceSection("# A\n## Pelly rules\n- [a] old\n", "## Pelly rules\n- [b] new\n");
        Assert.Equal("# A\n## Pelly rules\n- [b] new\n", after);
    }

    [Fact]
    public void Parse_ErrorOnEmptyText()
    {
        var (rules, errors) = RulesDocument.Parse("## Pelly rules\n- [a]  \n");
        Assert.Empty(rules);
        Assert.Equal(2, Assert.Single(errors).Line);
    }

    [Fact]
    public void Parse_ErrorOnMissingTextBeforeForbid()
    {
        var (rules, errors) = RulesDocument.Parse("## Pelly rules\n- [a] | forbid: x\n");
        Assert.Empty(rules);
        Assert.Equal(2, Assert.Single(errors).Line);
    }

    [Fact]
    public void Parse_ErrorOnBlankForbid()
    {
        var (rules, errors) = RulesDocument.Parse("## Pelly rules\n- [a] x | forbid:   \n");
        Assert.Empty(rules);
        Assert.Equal(2, Assert.Single(errors).Line);
    }
}
