using S6Planning;

public class PlanParserTests
{
    [Fact] public void Parses_line_with_symbol_and_group()
    {
        var p = PlanParser.Parse("- [ ] T1 cs Modern/Shop.Api/Services/IOrderService.cs#CancelAsync 반환 형식을 바꾼다 [G1]");
        var t = Assert.Single(p.Tasks);
        Assert.Equal(("T1", "cs", "Modern/Shop.Api/Services/IOrderService.cs", (string?)"CancelAsync", "반환 형식을 바꾼다", (string?)"G1"),
                     (t.Id, t.Kind, t.Path, t.Symbol, t.Request, t.Group));
        Assert.True(p.Parsed);
    }

    [Fact] public void Sql_line_without_symbol_or_group()
    {
        var t = Assert.Single(PlanParser.Parse("- [ ] T2 sql Sql/objects/usp_CancelOrder.sql 오류를 낸다").Tasks);
        Assert.Null(t.Symbol); Assert.Null(t.Group); Assert.Equal("오류를 낸다", t.Request);
    }

    [Fact] public void Fences_and_prose_are_extra_not_invalid()
    {
        var p = PlanParser.Parse("Here is the plan:\n```\n- [ ] T1 cs A.cs#M 고친다\n```\n");
        Assert.Single(p.Tasks); Assert.Empty(p.InvalidLines); Assert.Equal(3, p.ExtraLines);
    }

    [Fact] public void Checkbox_line_with_bad_kind_is_invalid()
    {
        var p = PlanParser.Parse("- [ ] T1 java A.java#M 고친다\n- [ ] T2 cs A.cs#M 고친다");
        Assert.Single(p.InvalidLines); Assert.False(p.Parsed);
    }

    [Fact] public void Missing_request_is_invalid() =>
        Assert.Single(PlanParser.Parse("- [ ] T1 cs A.cs#M").InvalidLines);

    [Fact] public void Empty_output_is_not_parsed() => Assert.False(PlanParser.Parse("").Parsed);
}
