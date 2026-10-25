using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Impact;

public class SemanticImpactFinderFallbackTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static Workspace Ws => new(Root, P(".pelly"));

    private sealed class FakeLoader(Func<SemanticLoad>? result = null) : ISemanticLoader
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct)
        {
            Calls.Add(projects);
            return Task.FromResult(result is null ? throw new InvalidOperationException("not expected") : result());
        }
    }

    private static MockFileSystem AppWithCaller()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("App/App.csproj"), new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"/>"));
        fs.AddFile(P("App/Lib.cs"), new MockFileData("class Lib { public int Run() => 1; }"));
        fs.AddFile(P("App/Use.cs"), new MockFileData("class Use { int Go() => new Lib().Run(); }"));
        return fs;
    }

    [Fact]
    public async Task Load_failure_falls_back_to_names_with_its_kind()
    {
        var fs = AppWithCaller();
        var loader = new FakeLoader(() => new SemanticLoad(null, new SemanticFailure(SemanticFailureKind.LowResolution, Rate: 0.028), null));
        var seed = new ImpactSeed("T1", 1, "App/Lib.cs", "Lib", "Run");

        var result = await new SemanticImpactFinder(fs, loader).FindAsync(Ws, [seed], TestContext.Current.CancellationToken);

        Assert.Equal(ImpactMode.Name, result.Mode);
        Assert.Equal(SemanticFailureKind.LowResolution, result.Fallback?.Kind);
        Assert.Equal("해석률 2.8%", result.Fallback?.Display);
        var call = Assert.Single(loader.Calls);
        Assert.Equal([P("App/App.csproj")], call);
        Assert.Equal([new ImpactTarget("T1", "App/Use.cs", "Go", "호출")], result.Targets);
    }

    [Fact]
    public async Task No_project_falls_back_without_loading()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("loose/Lib.cs"), new MockFileData("class Lib { public int Run() => 1; }"));
        fs.AddFile(P("loose/Use.cs"), new MockFileData("class Use { int Go() => new Lib().Run(); }"));
        var loader = new FakeLoader();
        var seed = new ImpactSeed("T1", 1, "loose/Lib.cs", "Lib", "Run");

        var result = await new SemanticImpactFinder(fs, loader).FindAsync(Ws, [seed], TestContext.Current.CancellationToken);

        Assert.Empty(loader.Calls);
        Assert.Equal(ImpactMode.Name, result.Mode);
        Assert.Equal(SemanticFailureKind.NoProject, result.Fallback?.Kind);
        Assert.Equal([new ImpactTarget("T1", "loose/Use.cs", "Go", "호출")], result.Targets);
    }

    [Fact]
    public async Task Scope_exception_falls_back_as_ScopeUnknown()
    {
        var fs = AppWithCaller();
        fs.AddFile(P("Bad/Bad.csproj"), new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"$(Dir)\\X.csproj\" /></ItemGroup></Project>"));
        // Outside every project: found only because the name search covers the whole workspace.
        fs.AddFile(P("loose/Other.cs"), new MockFileData("class Other { int Go2() => new Lib().Run(); }"));
        var loader = new FakeLoader();
        var seed = new ImpactSeed("T1", 1, "App/Lib.cs", "Lib", "Run");

        var result = await new SemanticImpactFinder(fs, loader).FindAsync(Ws, [seed], TestContext.Current.CancellationToken);

        Assert.Empty(loader.Calls);
        Assert.Equal(ImpactMode.Name, result.Mode);
        Assert.Equal(SemanticFailureKind.ScopeUnknown, result.Fallback?.Kind);
        Assert.Equal(
        [
            new ImpactTarget("T1", "App/Use.cs", "Go", "호출"),
            new ImpactTarget("T1", "loose/Other.cs", "Go2", "호출"),
        ], result.Targets);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var fs = AppWithCaller();
        var loader = new FakeLoader(() => throw new OperationCanceledException());
        var seed = new ImpactSeed("T1", 1, "App/Lib.cs", "Lib", "Run");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SemanticImpactFinder(fs, loader).FindAsync(Ws, [seed], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancelled_token_throws_before_any_search()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("loose/Lib.cs"), new MockFileData("class Lib { public int Run() => 1; }"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var seed = new ImpactSeed("T1", 1, "loose/Lib.cs", "Lib", "Run");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SemanticImpactFinder(fs, new FakeLoader()).FindAsync(Ws, [seed], cts.Token));
    }

    [Fact]
    public void Default_is_the_semantic_finder()
    {
        Assert.IsType<SemanticImpactFinder>(ImpactFinders.Default(new MockFileSystem()));
    }
}
