using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public sealed class WorkCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-eval-copy-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    [Fact]
    public void Copy_ExcludesBinObjPelly()
    {
        var from = Path.Combine(_root, "from");
        foreach (var dir in new[] { "Shop/bin/Debug", "Shop/obj", ".pelly/runs", "Shop/Services", "Shop/Services/obj" })
            Directory.CreateDirectory(Path.Combine(from, dir));
        File.WriteAllText(Path.Combine(from, "Shop", "Shop.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(from, "Shop", "Services", "A.cs"), "class A {}");
        File.WriteAllText(Path.Combine(from, "Shop", "bin", "Debug", "Shop.dll"), "x");
        File.WriteAllText(Path.Combine(from, ".pelly", "runs", "x.json"), "{}");

        var to = Path.Combine(_root, "to");
        WorkCopy.CopyDirectory(from, to);

        Assert.True(File.Exists(Path.Combine(to, "Shop", "Shop.csproj")));
        Assert.True(File.Exists(Path.Combine(to, "Shop", "Services", "A.cs")));
        Assert.False(Directory.Exists(Path.Combine(to, "Shop", "bin")));
        Assert.False(Directory.Exists(Path.Combine(to, "Shop", "obj")));
        Assert.False(Directory.Exists(Path.Combine(to, "Shop", "Services", "obj")));
        Assert.False(Directory.Exists(Path.Combine(to, ".pelly")));
    }

    [Fact]
    public void Prepare_BuiltInModernTask_SameLayoutAsBefore()
    {
        var suite = TestPaths.Suite;
        var task = TestPaths.Resolve("cs-modern-001");
        var work = Path.Combine(_root, "work");

        var file = WorkCopy.Prepare(suite, task, work);

        Assert.Equal(Path.Combine(work, "Modern", "Shop.Api", "Services", "OrderService.cs"), file);
        Assert.True(File.Exists(file));
        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json" })
            Assert.True(File.Exists(Path.Combine(work, name)), name);
        Assert.True(Directory.Exists(Path.Combine(work, ".svn")));
        Assert.True(File.Exists(Path.Combine(work, "Modern", "Modern.sln")));
        Assert.False(Directory.Exists(Path.Combine(work, "Legacy")));
        Assert.False(Directory.Exists(Path.Combine(work, "Modern", "TestResults")));
    }

    [Fact]
    public void Prepare_ExcludesGitAndVsAtAnyDepth_KeepsPackages()
    {
        var source = Path.Combine(_root, "suite", "src-root");
        foreach (var f in new[] { ".git/HEAD", "sub/.git", "sub/.vs/x", "sub/bin/a.dll", "packages/Foo.1.0/lib/a.dll", "extra/big.bin", "src/A.cs" })
        {
            var path = Path.Combine(source, f.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
        var suiteDir = Path.Combine(_root, "suite");
        File.WriteAllText(Path.Combine(suiteDir, BenchmarkSuite.FileName),
            """{ "formatVersion": 1, "name": "s", "roots": [ { "name": "App", "path": "src-root", "exclude": ["extra"] } ] }""");
        var load = BenchmarkSuite.Load(suiteDir);
        Assert.Empty(load.Errors);
        var task = load.Suite!.Resolve(new BenchmarkTask { Id = "cs-001", Kind = "cs", File = "App/src/A.cs" }, new System.IO.Abstractions.FileSystem(), out var error);
        Assert.Null(error);
        var work = Path.Combine(_root, "work");

        WorkCopy.Prepare(load.Suite, task!, work);

        var copied = Directory.GetFiles(Path.Combine(work, "App"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(work, "App"), f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal);
        Assert.Equal(["packages/Foo.1.0/lib/a.dll", "src/A.cs"], copied);
    }

    [Fact]
    public void Copy_ExcludesSvnHgAndTfvcFoldersAtAnyDepth()
    {
        var from = Path.Combine(_root, "from-vcs");
        foreach (var f in new[] { ".svn/wc.db", "sub/.SVN/x", ".hg/store/x", "sub/.hg/y", "$tf/1/x", "sub/$TF/y", "sub/A.cs", ".svnignore" })
        {
            var path = Path.Combine(from, f.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
        var to = Path.Combine(_root, "to-vcs");

        WorkCopy.CopyDirectory(from, to);

        var copied = Directory.GetFiles(to, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(to, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal);
        Assert.Equal([".svnignore", "sub/A.cs"], copied);
    }

    [Fact]
    public void CopyJudges_UsesSuiteJudgesFolder()
    {
        var suite = TestPaths.Suite;
        var work = Path.Combine(_root, "work");

        var copied = WorkCopy.CopyJudges(suite, suite.Roots.Single(r => r.Name == "Modern"), work);

        Assert.Equal(Path.Combine(work, "judges", "Modern"), copied);
        Assert.True(File.Exists(Path.Combine(work, "judges", "Modern", "Shop.Api.JudgeTests", "Shop.Api.JudgeTests.csproj")));
        Assert.Null(WorkCopy.CopyJudges(suite, suite.Roots.Single(r => r.Name == "Sql"), work));
    }

    private (BenchmarkSuite Suite, SuiteTask Task) OverlaySuite(string rootJsonExtra = "", string support = "")
    {
        var suiteDir = Path.Combine(_root, "suite");
        foreach (var (f, text) in new[] { ("src-root/src/A.cs", "class A {}"), ("src-root/sub/agents.md", "old"), ("rules/App/AGENTS.md", "## Pelly rules\n"),
                     ("rules/App/src/A.cs", "class A { int x; }"), ("rules/top/AGENTS.md", "top") })
        {
            var path = Path.Combine(suiteDir, f.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        File.WriteAllText(Path.Combine(suiteDir, BenchmarkSuite.FileName),
            $$"""{ "formatVersion": 1, "name": "s", "roots": [ { "name": "App", "path": "src-root"{{rootJsonExtra}} } ]{{support}} }""");
        var load = BenchmarkSuite.Load(suiteDir);
        Assert.Empty(load.Errors);
        var task = load.Suite!.Resolve(new BenchmarkTask { Id = "cs-001", Kind = "cs", File = "App/src/A.cs" }, new System.IO.Abstractions.FileSystem(), out var error);
        Assert.Null(error);
        return (load.Suite, task!);
    }

    [Fact]
    public void Prepare_Overlay_CopiedOverTheRoot()
    {
        var (suite, task) = OverlaySuite(""", "overlay": "rules/App" """);
        var work = Path.Combine(_root, "work");

        var file = WorkCopy.Prepare(suite, task, work);

        Assert.Equal("## Pelly rules\n", File.ReadAllText(Path.Combine(work, "App", "AGENTS.md")));
        Assert.Equal("class A { int x; }", File.ReadAllText(file)); // the overlay wins
        Assert.Equal("class A {}", File.ReadAllText(Path.Combine(_root, "suite", "src-root", "src", "A.cs"))); // original untouched
    }

    [Fact]
    public void Prepare_RemoveRules_DeletesEveryAgentsMd()
    {
        var (suite, task) = OverlaySuite(""", "overlay": "rules/App" """, """, "supportFiles": ["rules/top/AGENTS.md"]""");
        var work = Path.Combine(_root, "work");

        WorkCopy.Prepare(suite, task, work, removeRules: true);

        Assert.DoesNotContain(Directory.GetFiles(work, "*", SearchOption.AllDirectories),
            f => string.Equals(Path.GetFileName(f), "AGENTS.md", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(work, "App", "src", "A.cs")));
    }

    [Fact]
    public void Prepare_WithoutRemoveRules_KeepsAgentsMd()
    {
        var (suite, task) = OverlaySuite("", """, "supportFiles": ["rules/top/AGENTS.md"]""");
        var work = Path.Combine(_root, "work");

        WorkCopy.Prepare(suite, task, work);

        Assert.True(File.Exists(Path.Combine(work, "AGENTS.md")));
        Assert.True(File.Exists(Path.Combine(work, "App", "sub", "agents.md")));
    }
}
