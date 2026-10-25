using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Impact;

public class NameImpactFinderTests
{
    private static readonly SemanticFailure Fallback = new(SemanticFailureKind.NotRestored);
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Same_name_interface_is_a_known_false_hit()
    {
        var root = ImpactFixture.SameName();
        var fs = new FileSystem();
        var ws = new Workspace(root, Path.Combine(root, ".pelly"));
        var seed = new ImpactSeed("T1", 5, "Data/IOrderDao.cs", "IOrderDao", "GetOrder");
        var projects = ImpactScope.Projects(fs, ws, [Path.Combine(root, "Data", "IOrderDao.cs")]);

        var result = NameImpactFinder.Find(fs, ws, [seed], projects, Fallback);

        Assert.Equal(ImpactMode.Name, result.Mode);
        Assert.Same(Fallback, result.Fallback);
        Assert.Empty(result.UnsupportedSeeds);
        Assert.Equal(
        [
            new ImpactTarget("T1", "Data/OrderDao.cs", "GetOrder", "구현"),
            new ImpactTarget("T1", "Web.Tests/FakeOrderDao.cs", "GetOrder", "구현"),
            new ImpactTarget("T1", "Web/OrderController.cs", "Detail", "호출"),
            new ImpactTarget("T1", "Web/OrderService.cs", "GetOrder", "호출"),
            new ImpactTarget("T1", "Web/OrderService.cs", "Cancel", "호출"),
        ], result.Targets);
    }

    [Fact]
    public void Caller_in_constructor_and_field_initializer_is_reported()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("Lib.cs"), new MockFileData("class Lib { public int Run() => 1; }"));
        fs.AddFile(P("InCtor.cs"), new MockFileData("class InCtor { InCtor() { new Lib().Run(); } }"));
        fs.AddFile(P("InField.cs"), new MockFileData("class InField { int _x = new Lib().Run(); }"));
        var seed = new ImpactSeed("T1", 1, "Lib.cs", null, "Run");

        var result = NameImpactFinder.Find(fs, new Workspace(Root, P(".pelly")), [seed], [], Fallback);

        Assert.Equal(
        [
            new ImpactTarget("T1", "InCtor.cs", "InCtor", "호출"),
            new ImpactTarget("T1", "InField.cs", "InField", "멤버 밖"),
        ], result.Targets);
    }

    [Fact]
    public void Seed_itself_is_never_a_target()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("Lib.cs"), new MockFileData("class Lib { public int Run(int n) => n == 0 ? 1 : Run(n - 1); }"));
        var seed = new ImpactSeed("T1", 1, "Lib.cs", "Lib", "Run");

        var result = NameImpactFinder.Find(fs, new Workspace(Root, P(".pelly")), [seed], [], Fallback);

        Assert.Empty(result.Targets);
    }

    [Fact]
    public void Non_method_seed_is_unsupported()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("Lib.cs"), new MockFileData("class Lib { int _count; }"));
        var seed = new ImpactSeed("T7", 1, "Lib.cs", "Lib", "_count");

        var result = NameImpactFinder.Find(fs, new Workspace(Root, P(".pelly")), [seed], [], Fallback);

        Assert.Equal(["T7"], result.UnsupportedSeeds);
        Assert.Empty(result.Targets);
    }

    [Fact]
    public void Truncated_walk_is_reported_as_incomplete()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("Lib.cs"), new MockFileData("class Lib { public int Run() => 1; }"));
        fs.AddFile(P("a/A.cs"), new MockFileData("class A { void F() { new Lib().Run(); } }"));
        fs.AddFile(P("b/B.cs"), new MockFileData("class B { void F() { new Lib().Run(); } }"));
        var seed = new ImpactSeed("T1", 1, "Lib.cs", null, "Run");
        var ws = new Workspace(Root, P(".pelly"));

        Assert.True(NameImpactFinder.Find(fs, ws, [seed], [], Fallback, maxDirectories: 2).Incomplete);
        Assert.False(NameImpactFinder.Find(fs, ws, [seed], [], Fallback).Incomplete);
    }

    [Fact]
    public void Implementation_through_an_intermediate_base_class_is_found()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("I.cs"), new MockFileData("interface I { void M(); }"));
        fs.AddFile(P("C.cs"), new MockFileData("class C : I { public virtual void M() {} }"));
        fs.AddFile(P("D.cs"), new MockFileData("class D : C { public override void M() {} }"));
        var seed = new ImpactSeed("T1", 1, "I.cs", "I", "M");

        var result = NameImpactFinder.Find(fs, new Workspace(Root, P(".pelly")), [seed], [], Fallback);

        Assert.Equal(
        [
            new ImpactTarget("T1", "C.cs", "M", "구현"),
            new ImpactTarget("T1", "D.cs", "M", "재정의"),
        ], result.Targets);
    }
}
