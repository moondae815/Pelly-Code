using S6Planning;

public class CallerFinderTests
{
    private static readonly PlanTask Seed = new("T1", "cs", "Mini/IRepo.cs", "Save", "(seed)", null);

    [Fact] public void Finds_seed_implementers_including_nested_fake_then_callers()
    {
        var text = CallerFinder.Plan(TestPaths.Fixture("mini-corpus"), new[] { "Mini" }, Seed);
        var parsed = PlanParser.Parse(text);
        Assert.Equal(new[] { "mini/irepo.cs#Save", "mini/repo.cs#Save", "mini/tests.cs#Save", "mini/svc.cs#Run" },
            parsed.Tasks.Select(TargetKey.Of).ToArray());
        Assert.All(parsed.Tasks, t => Assert.Equal("G1", t.Group));
        Assert.True(parsed.Parsed);
    }

    [Fact] public void Non_calling_members_are_not_included() =>
        Assert.DoesNotContain("#Idle", CallerFinder.Plan(TestPaths.Fixture("mini-corpus"), new[] { "Mini" }, Seed));

    [Fact] public void Unknown_seed_member_throws() =>
        Assert.Throws<InvalidOperationException>(() => CallerFinder.Plan(TestPaths.Fixture("mini-corpus"), new[] { "Mini" },
            Seed with { Symbol = "Nope" }));
}
