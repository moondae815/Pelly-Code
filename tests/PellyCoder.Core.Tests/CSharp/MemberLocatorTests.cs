using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CSharp;

public sealed class MemberLocatorTests
{
    private const LanguageVersion V = LanguageVersion.CSharp12;

    [Fact]
    public void FindByName_CancelAsync_ReturnsOneWithSpanIncludingDocComment()
    {
        var path = CorpusLocation.Get("Modern/Shop.Api/Services/OrderService.cs");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var lines = text.Split('\n');

        var found = MemberLocator.FindByName(text, V, null, "CancelAsync");

        var one = Assert.Single(found);
        Assert.Equal("OrderService.CancelAsync(int, string)", one.Display);
        Assert.Equal("OrderService", one.ContainingType);
        Assert.Contains("/// <summary>", lines[one.Span.StartLine - 1]);
        Assert.Contains("public async Task CancelAsync", lines[one.Span.StartLine + 2]);
        Assert.Equal("        }", lines[one.Span.EndLine - 1]);
        Assert.Contains("public async Task<decimal> CalculateRefundAsync", lines[one.Span.EndLine + 5]);
    }

    [Fact]
    public void FindByName_Overloads_ReturnsAll()
    {
        const string src = "class C\n{\n    void M(int a) { }\n    void M(string s) { }\n}\n";

        var found = MemberLocator.FindByName(src, V, null, "M");

        Assert.Equal(["C.M(int)", "C.M(string)"], found.Select(f => f.Display));
        Assert.Equal(new[] { 3, 4 }, found.Select(f => f.Span.StartLine));
    }

    [Fact]
    public void FindByName_ClassFilter_Disambiguates()
    {
        const string src = "class A { void Run() { } }\nclass B { void Run() { } }\n";

        Assert.Equal(2, MemberLocator.FindByName(src, V, null, "Run").Count);
        var only = Assert.Single(MemberLocator.FindByName(src, V, "B", "Run"));
        Assert.Equal("B.Run()", only.Display);
        Assert.Empty(MemberLocator.FindByName(src, V, "C", "Run"));
    }

    [Fact]
    public void FindByName_Constructor_And_Property()
    {
        const string src = "class OrderService\n{\n    public string Name { get; set; }\n    public OrderService(int x, string y) { }\n}\n";

        var ctor = Assert.Single(MemberLocator.FindByName(src, V, null, "OrderService"));
        Assert.Equal("OrderService.OrderService(int, string)", ctor.Display);
        Assert.Equal(4, ctor.Span.StartLine);

        var prop = Assert.Single(MemberLocator.FindByName(src, V, null, "Name"));
        Assert.Equal("OrderService.Name", prop.Display);
        Assert.Equal(3, prop.Span.StartLine);
    }

    [Fact]
    public void FindByName_CaseSensitive_NoMatch()
    {
        var text = File.ReadAllText(CorpusLocation.Get("Modern/Shop.Api/Services/OrderService.cs")).Replace("\r\n", "\n");

        Assert.Empty(MemberLocator.FindByName(text, V, null, "cancelasync"));
    }

    [Fact]
    public void FindByName_NestedTypes_UseDottedContainingType()
    {
        const string src = "class Outer\n{\n    class Inner\n    {\n        void M() { }\n    }\n    void M() { }\n}\n";

        var all = MemberLocator.FindByName(src, V, null, "M");

        Assert.Equal(["Outer.Inner.M()", "Outer.M()"], all.Select(f => f.Display));
        Assert.Equal("Outer.Inner", all[0].ContainingType);
        var inner = Assert.Single(MemberLocator.FindByName(src, V, "Inner", "M"));
        Assert.Equal(5, inner.Span.StartLine);
    }

    [Fact]
    public void FindByName_SpanIncludesAttributesAndDocButNotPlainComments()
    {
        const string src = "class C\n{\n    // plain\n    /// <summary>x</summary>\n    /// more\n    [Obsolete]\n    [Foo]\n    void M()\n    {\n    }\n}\n";

        var one = Assert.Single(MemberLocator.FindByName(src, V, null, "M"));

        Assert.Equal(4, one.Span.StartLine);
        Assert.Equal(10, one.Span.EndLine);
    }

    [Fact]
    public void FindByName_PlainCommentOnly_StartsAtDeclaration()
    {
        const string src = "class C\n{\n    // plain\n    void M() { }\n}\n";

        Assert.Equal(4, Assert.Single(MemberLocator.FindByName(src, V, null, "M")).Span.StartLine);
    }
}
