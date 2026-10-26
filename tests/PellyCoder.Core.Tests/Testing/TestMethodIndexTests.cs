using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.Testing;

public class TestMethodIndexTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static MockFileSystem Fs(params (string Path, string Text)[] files)
    {
        var fs = new MockFileSystem();
        foreach (var (path, text) in files) fs.AddFile(P(path), new MockFileData(text));
        return fs;
    }

    [Fact]
    public void Find_OnlyAttributedMethods()
    {
        var fs = Fs(
            ("Shop/Orders.cs", "namespace Shop; class Orders { public void Cancel() { } }"),
            ("Shop.Tests/OrderTests.cs", "namespace Shop.Tests; class OrderTests { [Fact] public void Cancel() { } }"));

        var found = TestMethodIndex.Find(fs, Root, "Cancel");

        var one = Assert.Single(found);
        Assert.Equal("Shop.Tests.OrderTests.Cancel", one.FullName);
    }

    [Fact]
    public void Find_SameMethodInTwoClasses_ReturnsBoth()
    {
        var fs = Fs(
            ("T/A.cs", "namespace N; class OrderServiceTests { [Fact] public void Cancel_Throws() { } }"),
            ("T/B.cs", "namespace N; class OrderControllerTests { [Fact] public void Cancel_Throws() { } }"));

        Assert.Equal(2, TestMethodIndex.Find(fs, Root, "Cancel_Throws").Count);
        Assert.Single(TestMethodIndex.Find(fs, Root, "OrderServiceTests.Cancel_Throws"));
    }

    [Fact]
    public void Find_RecognizesAttributeForms()
    {
        var fs = Fs(("T/A.cs", """
            namespace N;
            class C
            {
                [Xunit.Fact] public void A() { }
                [FactAttribute] public void B() { }
                [TestMethod] public void D() { }
                [Theory, InlineData(1)] public void E(int x) { }
                [Test] public void F() { }
                [TestCase(1)] public void G(int x) { }
                [DataTestMethod] public void H() { }
                [Obsolete] public void Not() { }
            }
            """));

        var names = TestMethodIndex.Find(fs, Root, "C.A").Concat(TestMethodIndex.Find(fs, Root, "C.B"))
            .Concat(TestMethodIndex.Find(fs, Root, "C.D")).Concat(TestMethodIndex.Find(fs, Root, "C.E"))
            .Concat(TestMethodIndex.Find(fs, Root, "C.F")).Concat(TestMethodIndex.Find(fs, Root, "C.G"))
            .Concat(TestMethodIndex.Find(fs, Root, "C.H")).Select(d => d.FullName).ToList();

        Assert.Equal(7, names.Count);
        Assert.Empty(TestMethodIndex.Find(fs, Root, "C.Not"));
    }

    [Fact]
    public void Find_NestedAndFileScopedNamespace()
    {
        var fs = Fs(("T/A.cs", "namespace A.B; class Outer { class Inner { [Fact] public void M() { } } }"));

        var found = Assert.Single(TestMethodIndex.Find(fs, Root, "Outer.Inner.M"));
        Assert.Equal("A.B.Outer.Inner.M", found.FullName);
    }

    [Fact]
    public void Find_BlockNamespace()
    {
        var fs = Fs(("T/A.cs", "namespace A { namespace B { class C { [Fact] public void M() { } } } }"));

        Assert.Equal("A.B.C.M", Assert.Single(TestMethodIndex.Find(fs, Root, "C.M")).FullName);
    }

    [Fact]
    public void Find_ReportsNearestCsproj()
    {
        var fs = Fs(
            ("X.Tests/X.Tests.csproj", "<Project />"),
            ("X.Tests/Sub/A.cs", "namespace N; class C { [Fact] public void M() { } }"));

        var found = Assert.Single(TestMethodIndex.Find(fs, Root, "C.M"));
        Assert.Equal(P("X.Tests/X.Tests.csproj"), found.Csproj);
        Assert.Equal(P("X.Tests/Sub/A.cs"), found.File);
    }

    [Fact]
    public void Find_ResultsAreOrderedByFullName()
    {
        var fs = Fs(
            ("T/A.cs", "namespace Z; class C { [Fact] public void M() { } }"),
            ("T/B.cs", "namespace A; class C { [Fact] public void M() { } }"));

        Assert.Equal(["A.C.M", "Z.C.M"], TestMethodIndex.Find(fs, Root, "C.M").Select(d => d.FullName));
    }
}
