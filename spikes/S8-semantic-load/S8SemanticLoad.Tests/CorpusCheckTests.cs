using S6Planning;
using S8SemanticLoad;

public class CorpusCheckTests
{
    [Fact] public async Task A3_matches_gold_on_legacy_sources()
    {
        var r = await CorpusCheck.CheckA3Async(CorpusFixture.LegacyAsAdhoc(), TestPaths.Corpus, TestPaths.S6Cases);
        Assert.Empty(r.ForbiddenHit);
        Assert.Empty(r.Missing);
        Assert.True(r.Matches);
    }

    [Fact] public async Task A3_reports_forbidden_when_name_matching_is_used()
    {
        var found = await CorpusCheck.NameOnlyKeysAsync(CorpusFixture.LegacyAsAdhoc(), TestPaths.Corpus);
        var gold = GoldLoader.Load(Path.Combine(TestPaths.S6Cases, "a3-rename-getorder"));
        var r = CorpusCheck.Compare(found, gold);
        Assert.Contains("legacy/shop.web/controllers/ordercontroller.cs#Detail", r.ForbiddenHit);
    }
}
