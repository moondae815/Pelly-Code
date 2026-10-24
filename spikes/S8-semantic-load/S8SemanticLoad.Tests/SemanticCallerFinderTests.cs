using S6Planning;
using S8SemanticLoad;

public class SemanticCallerFinderTests
{
    private static GoldCase Gold(string id) => GoldLoader.Load(Path.Combine(TestPaths.S6Cases, id));

    // a3: 이름이 같은 다른 인터페이스(IOrderService.GetOrder)의 호출부를 끌어오지 않고 정답 5곳을 모두 낸다.
    [Fact] public async Task A3_semantic_plan_matches_gold_without_forbidden()
    {
        var gold = Gold("a3-rename-getorder");
        var text = await SemanticCallerFinder.PlanAsync(CorpusFixture.LegacyAsAdhoc(), TestPaths.Corpus, gold.Tasks[0], TestContext.Current.CancellationToken);
        var score = Scorer.Score(gold, CorpusIndexBuilder.Build(TestPaths.Corpus, gold.Scope), "CS", 1, text);
        Assert.Equal((1.0, 0, 0), (score.Recall, score.Forbidden, score.Hallucinated));
        Assert.True(score.Usable, text);
    }

    [Fact] public async Task Seed_comes_first_and_lines_use_the_plan_grammar()
    {
        var gold = Gold("a3-rename-getorder");
        var text = await SemanticCallerFinder.PlanAsync(CorpusFixture.LegacyAsAdhoc(), TestPaths.Corpus, gold.Tasks[0], TestContext.Current.CancellationToken);
        var plan = PlanParser.Parse(text);
        Assert.True(plan.Parsed);
        Assert.Equal("Legacy/Shop.Data/IOrderDao.cs", plan.Tasks[0].Path);
        Assert.All(plan.Tasks, t => Assert.Equal("G1", t.Group));
    }

    [Fact] public async Task Same_name_on_another_type_is_not_a_caller()
    {
        var s = AdhocFixture.Build(("P", [], [("/c/P/Code.cs", """
            namespace N {
              public interface IFoo { int Get(); }
              public interface IBar { int Get(); }
              public class Foo : IFoo { public int Get() => 1; }
              public class UsesFoo { public int A(IFoo f) => f.Get(); }
              public class UsesBar { public int B(IBar b) => b.Get(); }
            }
            """)]));
        var seed = new PlanTask("T1", "cs", "P/Code.cs", "Get", "x", "G1");
        var text = await SemanticCallerFinder.PlanAsync(s, "/c", seed, TestContext.Current.CancellationToken);
        Assert.Contains("#A ", text);
        Assert.DoesNotContain("#B ", text);
        Assert.Contains("P/Code.cs#Get", text);
    }
}
