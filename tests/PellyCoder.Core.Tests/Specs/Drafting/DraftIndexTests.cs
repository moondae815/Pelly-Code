using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Specs.Drafting;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs.Drafting;

// Golden files come from the S6 spike, not from this code:
//   dotnet run --project spikes/S6-planning/S6Planning -- index b1-block-cancel-shipped   (scope [Legacy, Sql]) -> index-b1.txt
//   dotnet run --project spikes/S6-planning/S6Planning -- index d1-modern-cancel-shipped  (scope [Modern])      -> index-d1.txt
// keeping only the Render() part (the spike's last "tokens=... prompt=..." line is dropped). The spike's "Sql" scope means
// Sql/objects/*.sql only, so the product scope for b1 is the folders Legacy and Sql/objects. Workspace root = eval/corpus.
public sealed class DraftIndexTests
{
    private const int Level = 170;

    private static string Fixture(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tests", "fixtures", "spec-draft", name);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(name);
    }

    private static string RenderCorpus(params string[] scopes)
    {
        var fs = new FileSystem();
        var root = CorpusLocation.Get("");
        var ws = new Workspace(root, Path.Combine(root, ".pelly"));
        var (index, error) = DraftIndexBuilder.Build(fs, ws, scopes.Select(s => Path.Combine(root, s)).ToList(), Level);
        Assert.Null(error);
        Assert.Empty(index!.Skipped);
        return index.Render();
    }

    [Fact]
    public void Render_MatchesS6_B1() =>
        Assert.Equal(Fixture("index-b1.txt"), RenderCorpus("Legacy", "Sql/objects"));

    [Fact]
    public void Render_MatchesS6_D1() =>
        Assert.Equal(Fixture("index-d1.txt"), RenderCorpus("Modern"));

    private static MockFileSystem Fs()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/ws/App.sln", "");
        fs.AddFile("/ws/src/a.cs", "class A { public int X { get; set; } public void M(int a) { } }");
        fs.AddFile("/ws/src/bin/x.cs", "class X { }");
        fs.AddFile("/ws/src/obj/y.cs", "class Y { }");
        fs.AddFile("/ws/src/App.csproj", "<Project />");
        fs.AddFile("/ws/db/p.sql", "CREATE PROCEDURE dbo.usp_P @Id int AS SELECT 1;");
        return fs;
    }

    private static Workspace Ws() => new("/ws", "/ws/.pelly");

    private static DraftIndex Ok(MockFileSystem fs, params string[] scopes)
    {
        var (index, error) = DraftIndexBuilder.Build(fs, Ws(), scopes, Level);
        Assert.Null(error);
        return index!;
    }

    [Fact]
    public void Folder_TakesCsAndSql_SkipsBinObj()
    {
        var fs = Fs();
        Assert.Equal(["src/a.cs"], Ok(fs, "/ws/src").Files.Select(f => f.Path));
        Assert.Equal(["db/p.sql", "src/a.cs"], Ok(fs, "/ws/src", "/ws/db").Files.Select(f => f.Path));
    }

    [Fact]
    public void Csproj_TakesItsFolder() =>
        Assert.Equal(["src/a.cs"], Ok(Fs(), "/ws/src/App.csproj").Files.Select(f => f.Path));

    [Fact]
    public void Paths_AreWorkspaceRelativeWithSlash()
    {
        var index = Ok(Fs(), "/ws/src", "/ws/db");
        Assert.All(index.Files, f => Assert.DoesNotContain('\\', f.Path));
        Assert.All(index.Files, f => Assert.False(f.Path.StartsWith('/')));
        Assert.Equal(["cs", "sql"], index.Files.Select(f => f.Kind).Order());
    }

    [Fact]
    public void UnparsableSql_IsSkippedNotThrown()
    {
        var fs = Fs();
        fs.AddFile("/ws/db/bad.sql", "CREATE PROCEDURE ((( AS");
        var index = Ok(fs, "/ws/db", "/ws/src");
        Assert.Equal(["db/bad.sql"], index.Skipped);
        Assert.Equal(["db/p.sql", "src/a.cs"], index.Files.Select(f => f.Path));
    }

    [Fact]
    public void Scope_OutsideWorkspace_Rejected()
    {
        var fs = Fs();
        fs.AddFile("/other/z.cs", "class Z { }");
        var (index, error) = DraftIndexBuilder.Build(fs, Ws(), ["/other"], Level);
        Assert.Null(index);
        Assert.Contains("/other", error!.Message);
        Assert.Contains("작업 공간 밖", error.Message);
    }

    [Fact]
    public void Scope_Missing_Rejected()
    {
        var (index, error) = DraftIndexBuilder.Build(Fs(), Ws(), ["/ws/nope"], Level);
        Assert.Null(index);
        Assert.Contains("/ws/nope", error!.Message);
        Assert.Contains("찾을 수 없습니다", error.Message);
    }

    [Fact]
    public void SameFileTwice_ListedOnce() =>
        Assert.Single(Ok(Fs(), "/ws/src", "/ws/src/a.cs").Files);

    [Fact]
    public void ScopeAboveDirectoryCap_IsRejectedNotTruncated()
    {
        var fs = Fs();
        for (var i = 0; i <= 2000; i++) fs.AddFile($"/ws/big/d{i:D4}/f.cs", "class F { }");
        var (index, error) = DraftIndexBuilder.Build(fs, Ws(), ["/ws/big"], Level);
        Assert.Null(index);
        Assert.Contains("범위가 너무 넓어", error!.Message);
        Assert.Contains("/ws/big", error.Message);
    }
}
