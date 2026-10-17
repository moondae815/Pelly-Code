using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CSharp;

/// <summary><c>--line</c> lookup and the full member list (design 8.4).</summary>
public sealed class MemberLocatorLineTests
{
    private const LanguageVersion V = LanguageVersion.CSharp12;

    // Corpus line numbers (1-based): class 14, field 16, constructor 24-28, blank 29, CancelAsync doc 30-32,
    // CancelAsync body 33-96, CalculateRefundAsync 98-216 (lambda on 136), SearchAsync 218-271.
    private static string OrderService() =>
        File.ReadAllText(CorpusLocation.Get("Modern/Shop.Api/Services/OrderService.cs")).Replace("\r\n", "\n");

    private static string? At(int line) => MemberLocator.FindAtLine(OrderService(), V, line)?.Display;

    [Fact]
    public void Line_InsideMethodBody_ReturnsMethod()
    {
        var found = MemberLocator.FindAtLine(OrderService(), V, 45);

        Assert.NotNull(found);
        Assert.Equal("OrderService.CancelAsync(int, string)", found.Display);
        Assert.Equal("OrderService", found.ContainingType);
        Assert.Equal(new Models.TextSpanInfo(30, 96), found.Span);
    }

    [Fact]
    public void Line_OnDocComment_ReturnsMember()
    {
        Assert.Equal("OrderService.CancelAsync(int, string)", At(30));
        Assert.Equal("OrderService.CancelAsync(int, string)", At(31));
    }

    [Fact]
    public void Line_InsideLambda_ReturnsEnclosingMember()
    {
        Assert.Equal("OrderService.CalculateRefundAsync(int)", At(136));
    }

    [Fact]
    public void Line_InsideLocalFunction_ReturnsEnclosingMember()
    {
        const string src = "class C\n{\n    int M()\n    {\n        int Local()\n        {\n            return 1;\n        }\n        return Local();\n    }\n}\n";

        Assert.Equal("C.M()", MemberLocator.FindAtLine(src, V, 7)?.Display);
    }

    [Fact]
    public void Line_InsidePropertyAccessor_ReturnsProperty()
    {
        const string src =
            "class C\n{\n    private int _x;\n    public int X\n    {\n        get\n        {\n            return _x;\n        }\n        set { _x = value; }\n    }\n}\n";

        Assert.Equal("C.X", MemberLocator.FindAtLine(src, V, 8)?.Display);
        Assert.Equal("C.X", MemberLocator.FindAtLine(src, V, 10)?.Display);
    }

    [Fact]
    public void Line_InsideOperators_ReturnsOperator()
    {
        const string src =
            "struct P\n{\n    public static P operator +(P a, P b)\n    {\n        return a;\n    }\n" +
            "    public static implicit operator int(P p)\n    {\n        return 0;\n    }\n}\n";

        Assert.Equal("P.operator +(P, P)", MemberLocator.FindAtLine(src, V, 5)?.Display);
        Assert.Equal("P.operator int(P)", MemberLocator.FindAtLine(src, V, 9)?.Display);
    }

    [Fact]
    public void Line_OnField_ReturnsNull() => Assert.Null(At(16));

    [Fact]
    public void Line_OnBlankLineBetweenMembers_ReturnsNull() => Assert.Null(At(29));

    [Fact]
    public void Line_OnClassDeclaration_ReturnsNull() => Assert.Null(At(14));

    [Fact]
    public void Line_OnUsing_ReturnsNull() => Assert.Null(At(1));

    [Fact]
    public void Line_OutOfRange_ReturnsNull()
    {
        Assert.Null(At(0));
        Assert.Null(At(-1));
        Assert.Null(At(10_000));
    }

    [Fact]
    public void Line_PicksOverloadContainingLine()
    {
        const string src = "class C\n{\n    void M(int a)\n    {\n    }\n    void M(string s)\n    {\n    }\n}\n";

        Assert.Equal("C.M(int)", MemberLocator.FindAtLine(src, V, 3)?.Display);
        Assert.Equal("C.M(int)", MemberLocator.FindAtLine(src, V, 5)?.Display);
        Assert.Equal("C.M(string)", MemberLocator.FindAtLine(src, V, 6)?.Display);
        Assert.Equal("C.M(string)", MemberLocator.FindAtLine(src, V, 8)?.Display);
    }

    [Fact]
    public void Line_InNestedTypeMember_ReturnsInnermost()
    {
        const string src = "class Outer\n{\n    class Inner\n    {\n        void Run()\n        {\n        }\n    }\n}\n";

        var found = MemberLocator.FindAtLine(src, V, 6);

        Assert.Equal("Outer.Inner.Run()", found?.Display);
        Assert.Null(MemberLocator.FindAtLine(src, V, 3));
    }

    [Fact]
    public void AllMembers_SourceOrder()
    {
        var all = MemberLocator.AllMembers(OrderService(), V);

        Assert.Equal(
        [
            "OrderService.OrderService(IOrderRepository, ILogger<OrderService>)",
            "OrderService.CancelAsync(int, string)",
            "OrderService.CalculateRefundAsync(int)",
            "OrderService.SearchAsync(OrderSearchRequest)",
        ], all.Select(m => m.Display));
    }

    [Fact]
    public void AllMembers_SameKindsAsFindByName_NoOperators()
    {
        const string src = "struct P\n{\n    int _f;\n    public int X => 1;\n    public static P operator +(P a, P b) => a;\n    void M() { }\n}\n";

        Assert.Equal(["P.X", "P.M()"], MemberLocator.AllMembers(src, V).Select(m => m.Display));
    }
}
