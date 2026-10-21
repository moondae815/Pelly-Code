using S6Planning;

public class CorpusIndexTests
{
    [Fact] public void Modern_index_includes_nested_test_fake_member()
    {
        var idx = CorpusIndexBuilder.Build(TestPaths.Corpus, new[] { "Modern" });
        var f = idx.Files.Single(x => x.Path == "Modern/Shop.Api.Tests/OrderServiceTests.cs");
        Assert.Contains(f.Members, m => m.TypeName == "OrderServiceTests.FakeOrderRepository" && m.Name == "UpdateStatusAsync");
    }

    [Fact] public void Multi_object_sql_file_lists_each_object()
    {
        var f = CorpusIndexBuilder.Build(TestPaths.Corpus, new[] { "Sql" }).Files.Single(x => x.Path == "Sql/objects/Settlement_All.sql");
        Assert.Contains(f.Members, m => m.Name == "dbo.usp_SettleDaily");
        Assert.Contains(f.Members, m => m.Name == "dbo.usp_SettleMonthly");
    }

    [Fact] public void Sql_signature_lists_parameters() =>
        Assert.Contains("@OrderId int", CorpusIndexBuilder.Build(TestPaths.Corpus, new[] { "Sql" })
            .Files.Single(x => x.Path == "Sql/objects/usp_CancelOrder.sql").Members.Single().Signature);

    [Fact] public void Render_has_no_method_bodies() =>
        Assert.DoesNotContain("SqlConnection(", CorpusIndexBuilder.Build(TestPaths.Corpus, new[] { "Modern" }).Render());

    [Fact] public void Excludes_build_output_and_assembly_info()
    {
        var paths = CorpusIndexBuilder.Build(TestPaths.Corpus, new[] { "Legacy" }).Files.Select(f => f.Path).ToList();
        Assert.DoesNotContain(paths, p => p.Contains("/obj/") || p.Contains("/bin/") || p.EndsWith("AssemblyInfo.cs"));
        Assert.Contains("Legacy/Shop.Batch/SettlementBatch.cs", paths);
    }

    [Fact] public void Every_gold_target_and_forbidden_exists_in_its_scope_index()
    {
        foreach (var c in GoldLoader.LoadAll(TestPaths.Cases))
        {
            var idx = CorpusIndexBuilder.Build(TestPaths.Corpus, c.Scope);
            Assert.All(c.Tasks, t => Assert.True(idx.Exists(t), $"{c.Id} {t.Id}"));
            Assert.All(c.Forbidden, f => { var (p, _, s) = f.Partition('#'); Assert.True(idx.Exists("cs", p, s), f); });
        }
    }

    [Fact] public void Exists_rejects_unknown_member_and_wrong_kind()
    {
        var idx = CorpusIndexBuilder.Build(TestPaths.Corpus, new[] { "Modern" });
        Assert.False(idx.Exists("cs", "Modern/Shop.Api/Services/OrderService.cs", "RefundAsync"));
        Assert.False(idx.Exists("sql", "Modern/Shop.Api/Services/OrderService.cs", null));
        Assert.False(idx.Exists("cs", "Modern/Shop.Api/Services/OrderService.cs", null));
        Assert.True(idx.Exists("cs", "Modern/Shop.Api/Services/OrderService.cs", "OrderService.CancelAsync"));
    }

    [Fact] public void Sql_symbol_matches_with_or_without_schema()
    {
        var idx = CorpusIndexBuilder.Build(TestPaths.Corpus, new[] { "Sql" });
        Assert.True(idx.Exists("sql", "Sql/objects/Settlement_All.sql", "usp_SettleDaily"));
        Assert.True(idx.Exists("sql", "Sql/objects/Settlement_All.sql", "dbo.usp_SettleDaily"));
        Assert.False(idx.Exists("sql", "Sql/objects/Settlement_All.sql", "usp_Nope"));
    }
}
