using S8SemanticLoad;

public class SemanticMetricsTests
{
    [Fact] public async Task All_calls_resolve_in_clean_code()
    {
        var r = await SemanticMetrics.MeasureCallsAsync(AdhocFixture.Single("class A { void M(){ N(); System.Console.WriteLine(1); } void N(){} }"));
        Assert.Equal((2, 2), (r.Total, r.Resolved));
        Assert.Equal(1.0, r.Rate);
    }

    [Fact] public async Task Unknown_method_is_unresolved()
    {
        var r = await SemanticMetrics.MeasureCallsAsync(AdhocFixture.Single("class A { void M(){ Missing(); } }"));
        Assert.Equal(1, r.Unresolved);
    }

    [Fact] public async Task Overload_mismatch_is_ambiguous_or_unresolved_not_resolved()
    {
        var r = await SemanticMetrics.MeasureCallsAsync(AdhocFixture.Single("class A { void F(int a){} void F(string a){} void M(){ F(1.5); } }"));
        Assert.Equal(0, r.Resolved);
        Assert.Equal(1, r.Ambiguous + r.Unresolved);
    }

    [Fact] public async Task Nameof_and_dynamic_are_excluded()
    {
        var r = await SemanticMetrics.MeasureCallsAsync(AdhocFixture.Single("class A { void M(dynamic d){ var s = nameof(M); d.Foo(); N(); } void N(){} }"));
        Assert.Equal(1, r.Total);
    }

    [Fact] public async Task Health_counts_errors_and_missing_refs()
    {
        var s = AdhocFixture.Single("class A { int x = \"s\"; }", missingRef: "/nope/x.dll");
        var h = Assert.Single(await SemanticMetrics.MeasureHealthAsync(s, new ProjectCensus([])));
        Assert.True(h.CompileErrors >= 1);
        Assert.Equal(1, h.MissingMetadataRefs);
    }
}
