using S6Planning;

public class GoldTests
{
    [Theory]
    [InlineData("cs", @".\Modern\A.cs", "OrderService.CancelAsync()", "modern/a.cs#CancelAsync")]
    [InlineData("cs", "Modern/A.cs", "CancelAsync", "modern/a.cs#CancelAsync")]
    [InlineData("sql", "Sql/objects/x.sql", "dbo.usp_X", "sql/objects/x.sql")]
    [InlineData("cs", "eval/corpus/Modern/A.cs", "M", "eval/corpus/modern/a.cs#M")]
    public void TargetKey_normalizes_only_agreed_variants(string k, string p, string s, string expected) =>
        Assert.Equal(expected, TargetKey.Of(k, p, s));

    [Fact] public void Loads_all_twelve_drafts_consistently()
    {
        var all = GoldLoader.LoadAll(TestPaths.Cases);
        Assert.Equal(12, all.Count);
        Assert.Equal(new[] { 'A', 'B', 'C', 'D' }, all.Select(c => c.Category).Distinct().Order());
        Assert.All(all, c => Assert.Equal(3, all.Count(x => x.Category == c.Category)));
        Assert.All(all, c => Assert.NotEmpty(c.Required));
        Assert.All(all, c => Assert.DoesNotContain("#", c.Request));
    }

    [Fact] public void Front_matter_fields_are_read()
    {
        var c = GoldLoader.Load(Path.Combine(TestPaths.Cases, "a3-rename-getorder"));
        Assert.Equal(('A', "draft"), (c.Category, c.Status));
        Assert.Equal(new[] { "Legacy" }, c.Scope);
        Assert.Equal(5, c.Tasks.Count);
        Assert.Equal(2, c.Forbidden.Count);
        Assert.Contains(("T1", "T5"), c.Order);
    }

    [Fact] public void Escaped_quotes_in_request_are_unescaped() =>
        Assert.Contains("\"결제 전 주문 취소\"", GoldLoader.Load(Path.Combine(TestPaths.Cases, "d2-legacy-pending-history")).Request);

    [Fact] public void Constraint_pointing_to_missing_task_throws() =>
        Assert.Throws<InvalidDataException>(() => GoldLoader.Load(TestPaths.Fixture("bad-order-ref")));
}
