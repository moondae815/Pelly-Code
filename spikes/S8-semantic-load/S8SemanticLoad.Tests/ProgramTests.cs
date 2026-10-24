using S8SemanticLoad;

public class ProgramTests
{
    [Fact] public async Task Usage_error_exits_2() => Assert.Equal(2, await Program.RunAsync([]));

    [Fact] public async Task Missing_path_exits_2() => Assert.Equal(2, await Program.RunAsync(["load", "/nope/none.sln"]));

    [Fact] public async Task Unknown_option_exits_2() =>
        Assert.Equal(2, await Program.RunAsync(["load", Path.Combine(TestPaths.Corpus, "Modern", "Modern.sln"), "--bogus"]));
}
