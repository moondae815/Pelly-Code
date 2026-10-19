using System.IO.Abstractions;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public sealed class BenchmarkSuiteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-eval-suite-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FileSystem _fs = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    private string Suite(string name, string json, params string[] folders)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var f in folders) Directory.CreateDirectory(Path.Combine(dir, f));
        File.WriteAllText(Path.Combine(dir, BenchmarkSuite.FileName), json);
        return dir;
    }

    [Fact]
    public void Load_BuiltInSuite_HasThreeRootsAndSupportFiles()
    {
        var suite = TestPaths.Suite;

        Assert.Equal("builtin", suite.Name);
        Assert.Equal(["Modern", "Legacy", "Sql"], suite.Roots.Select(r => r.Name));
        Assert.All(suite.Roots, r => Assert.True(Path.IsPathFullyQualified(r.Path) && Directory.Exists(r.Path), r.Path));
        Assert.Equal(["Directory.Build.props", "Directory.Packages.props", "global.json"], suite.SupportFiles.Select(Path.GetFileName));
        Assert.EndsWith(Path.Combine("eval", "results"), suite.ResultsDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(suite.Dir, "tasks"), suite.TasksDir);
        Assert.Equal(Path.Combine(suite.Dir, "judges", "Modern"), suite.JudgesDir(suite.Roots[0]));
    }

    [Fact]
    public void Load_RejectsUnknownKey_NestedRoots_DuplicateNames_MissingPath()
    {
        var cases = new[]
        {
            Suite("unknown", """{ "formatVersion": 1, "name": "s", "roots": [ { "name": "A", "path": "a" } ], "resultDir": "r" }""", "a"),
            Suite("nested", """{ "formatVersion": 1, "name": "s", "roots": [ { "name": "A", "path": "a" }, { "name": "B", "path": "a/b" } ] }""", "a/b"),
            Suite("duplicate", """{ "formatVersion": 1, "name": "s", "roots": [ { "name": "A", "path": "a" }, { "name": "a", "path": "b" } ] }""", "a", "b"),
            Suite("missing", """{ "formatVersion": 1, "name": "s", "roots": [ { "name": "A", "path": "nope" } ] }"""),
        };

        foreach (var dir in cases)
        {
            var load = BenchmarkSuite.Load(dir);
            Assert.True(load.Errors.Count > 0, dir);
            Assert.Null(load.Suite);
        }
    }

    [Fact]
    public void Load_NoSuiteFile_IsError()
    {
        Directory.CreateDirectory(_root);

        var load = BenchmarkSuite.Load(_root);

        Assert.Null(load.Suite);
        Assert.Contains(BenchmarkSuite.FileName, Assert.Single(load.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RootPathWithSpaceAndKorean_Resolves()
    {
        var dir = Suite("korean", """{ "formatVersion": 1, "name": "현장", "roots": [ { "name": "Erp", "path": "ERP 본사/소스" } ] }""", "ERP 본사/소스");

        var load = BenchmarkSuite.Load(dir);

        Assert.Empty(load.Errors);
        var root = Assert.Single(load.Suite!.Roots);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "ERP 본사", "소스")), root.Path);
        Assert.Equal(Path.Combine(dir, "results"), load.Suite.ResultsDir);

        // Review Focus 4: the target resolves and is copied under work/<root name>/ with the space and Korean intact.
        var source = Path.Combine(root.Path, "주문 서비스", "Order.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "class Order {}");
        var task = load.Suite.Resolve(new BenchmarkTask { Id = "cs-001", Kind = "cs", File = "Erp/주문 서비스/Order.cs" }, _fs, out var error);
        Assert.Null(error);
        Assert.Equal(source, task!.SourceFile);
        Assert.Equal("주문 서비스/Order.cs", task.RelativeFile);

        var work = Path.Combine(_root, "작업 폴더");
        var copied = WorkCopy.Prepare(load.Suite, task, work);

        Assert.Equal(Path.Combine(work, "Erp", "주문 서비스", "Order.cs"), copied);
        Assert.True(File.Exists(copied));
        Assert.StartsWith(Path.Combine(work, "Erp") + Path.DirectorySeparatorChar, copied, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NullRootOrNulCharacter_IsErrorNotException()
    {
        var nullRoot = Suite("null-root", """{ "formatVersion": 1, "name": "s", "roots": [ null ] }""");
        var nul = Suite("nul", """{ "formatVersion": 1, "name": "s", "roots": [ { "name": "A", "path": "a\u0000b" } ], "supportFiles": ["x\u0000y"], "resultsDir": "r\u0000" }""");

        foreach (var dir in new[] { nullRoot, nul })
        {
            var load = BenchmarkSuite.Load(dir);
            Assert.Null(load.Suite);
            Assert.NotEmpty(load.Errors);
        }
        Assert.Contains("roots[0]", Assert.Single(BenchmarkSuite.Load(nullRoot).Errors), StringComparison.Ordinal);
        Assert.Equal(3, BenchmarkSuite.Load(nul).Errors.Count);
        Assert.Null(TestPaths.Suite.Resolve(new BenchmarkTask { Id = "cs-002", Kind = "cs", File = "Modern/a\0.cs" }, _fs, out var error));
        Assert.Contains("cs-002", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_FileStartsWithRootName_GivesAbsoluteSourceFile()
    {
        var task = new BenchmarkTask { Id = "cs-modern-001", Kind = "cs", File = "Modern/Shop.Api/Services/OrderService.cs" };

        var resolved = TestPaths.Suite.Resolve(task, _fs, out var error);

        Assert.Null(error);
        Assert.NotNull(resolved);
        var corpus = Path.Combine(Path.GetDirectoryName(TestPaths.Suite.Dir)!, "corpus");
        Assert.Equal(Path.Combine(corpus, "Modern", "Shop.Api", "Services", "OrderService.cs"), resolved.SourceFile);
        Assert.Equal("Shop.Api/Services/OrderService.cs", resolved.RelativeFile);
        Assert.Equal("Modern", resolved.Root.Name);
        Assert.False(resolved.IsLegacy);
    }

    [Theory]
    [InlineData("Nope/x.cs")]
    [InlineData("Modern/Shop.Api/Services/NoSuch.cs")]
    [InlineData("Modern/../Legacy/Shop.Web/Services/OrderService.cs")]
    [InlineData("Modern\\Shop.Api\\Services\\OrderService.cs")]
    [InlineData("Modern")]
    public void Resolve_UnknownRoot_Or_MissingFile_Or_DotDot_IsError(string file)
    {
        var task = new BenchmarkTask { Id = "cs-modern-777", Kind = "cs", File = file };

        var resolved = TestPaths.Suite.Resolve(task, _fs, out var error);

        Assert.Null(resolved);
        Assert.NotNull(error);
        Assert.Contains("cs-modern-777", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_BuiltInTasks_LegacyIsTheProjectStyle()
    {
        var suite = TestPaths.Suite;
        var tasks = BenchmarkTask.LoadAll(suite.TasksDir);
        Assert.Equal(50, tasks.Count);

        var resolved = tasks.Select(t => (Task: t, Resolved: suite.Resolve(t, _fs, out var e), Error: e)).ToList();

        Assert.All(resolved, r => Assert.True(r.Resolved is not null, r.Error));
        var legacy = resolved.Where(r => r.Resolved!.IsLegacy).ToList();
        Assert.Equal(18, legacy.Count);
        Assert.All(legacy, r => Assert.StartsWith("cs-legacy-", r.Task.Id, StringComparison.Ordinal));
        Assert.All(resolved.Where(r => r.Task.Kind == "sql"), r => Assert.False(r.Resolved!.IsLegacy));
    }
}
