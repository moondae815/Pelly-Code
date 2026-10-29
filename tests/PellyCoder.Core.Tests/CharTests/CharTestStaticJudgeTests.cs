using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class CharTestStaticJudgeTests
{
    private const LanguageVersion V = LanguageVersion.CSharp12;
    private const string Target = "CalculateRefundAsync";
    private static readonly FrameworkInfo X = TestFrameworks.Info(TestFramework.XunitV3);

    private static IReadOnlyList<CharTestRejection> Judge(
        string members, FrameworkInfo? fw = null, int count = 3, params string[] existing) =>
        CharTestStaticJudge.Judge(new CharTestOutput([], [members]), fw ?? X, count, Target,
            existing.ToHashSet(), V);

    private static string Fact(string name, string body = "var r = sut.CalculateRefundAsync(1); Assert.Equal(5, r);") =>
        $"[Fact]\npublic void {name}() {{ {body} }}";

    [Fact]
    public void Valid_Xunit_Ok() => Assert.Empty(Judge(Fact("A_B_C")));

    [Fact]
    public void Shape_TooMany_Rejected()
    {
        var src = string.Join("\n", Enumerable.Range(1, 4).Select(i => Fact($"T{i}")));
        var r = Judge(src, count: 3);
        Assert.Contains(r, x => x.Kind == FailureKinds.CharTestShape);
    }

    [Fact]
    public void Shape_NoTests_Rejected() =>
        Assert.Contains(Judge("private void H() { }"), x => x.Kind == FailureKinds.CharTestShape);

    [Fact]
    public void Shape_TheoryCountsAsTest()
    {
        var src = "[Theory]\n[InlineData(1)]\npublic void T(int a) { Assert.Equal(a, sut.CalculateRefundAsync(a)); }";
        Assert.Empty(Judge(src));
    }

    [Fact]
    public void Shape_PublicHelper_Rejected()
    {
        var r = Judge(Fact("A") + "\npublic int Helper() => 1;");
        Assert.Contains(r, x => x.Kind == FailureKinds.CharTestShape && x.TestName == "Helper");
    }

    [Fact]
    public void Shape_NameCollision_Rejected()
    {
        var r = Judge(Fact("CalculateRefundAsync_PaidOrder_RefundsItemsAndShippingFee"),
            existing: "CalculateRefundAsync_PaidOrder_RefundsItemsAndShippingFee");
        Assert.Contains(r, x => x.Kind == FailureKinds.CharTestShape);
    }

    [Fact]
    public void Shape_DuplicateAmongNew_Rejected() =>
        Assert.Contains(Judge(Fact("A") + "\n" + Fact("A")), x => x.Kind == FailureKinds.CharTestShape);

    [Fact]
    public void Shape_Skip_Rejected()
    {
        var src = "[Fact(Skip = \"x\")]\npublic void A() { var r = sut.CalculateRefundAsync(1); Assert.Equal(5, r); }";
        Assert.Contains(Judge(src), x => x.Kind == FailureKinds.CharTestShape);
    }

    [Fact]
    public void Shape_SkipInString_Ok() =>
        Assert.Empty(Judge(Fact("A", "var r = sut.CalculateRefundAsync(1); Assert.Equal(\"Skip = x\", r);")));

    [Fact]
    public void Shape_MsTestIgnoreAndInconclusive_Rejected()
    {
        var fw = TestFrameworks.Info(TestFramework.MsTest);
        var ignored = "[TestMethod]\n[Ignore]\npublic void A() { var r = sut.CalculateRefundAsync(1); Assert.AreEqual(5, r); }";
        var incon = "[TestMethod]\npublic void A() { Assert.Inconclusive(); var r = sut.CalculateRefundAsync(1); Assert.AreEqual(5, r); }";
        Assert.Contains(Judge(ignored, fw), x => x.Kind == FailureKinds.CharTestShape);
        Assert.Contains(Judge(incon, fw), x => x.Kind == FailureKinds.CharTestShape);
    }

    [Fact]
    public void Assert_None_Rejected()
    {
        var r = Judge(Fact("A", "var r = sut.CalculateRefundAsync(1);"));
        Assert.Contains(r, x => x.Kind == FailureKinds.CharTestNoAssert && x.TestName == "A");
    }

    [Fact]
    public void Assert_AllLiteral_Rejected()
    {
        var r = Judge(Fact("A", "sut.CalculateRefundAsync(1); Assert.True(true);"));
        Assert.Contains(r, x => x.Kind == FailureKinds.CharTestNoAssert);
    }

    [Fact]
    public void Assert_ThrowsCounts() =>
        Assert.Empty(Judge(Fact("A", "Assert.Throws<System.Exception>(() => sut.CalculateRefundAsync(1));")));

    [Fact]
    public void Call_ViaPrivateHelper_Ok()
    {
        var src = Fact("A", "var r = Run(); Assert.Equal(5, r);") + "\nprivate int Run() => sut.CalculateRefundAsync(1);";
        Assert.Empty(Judge(src));
    }

    [Fact]
    public void Call_Missing_Rejected()
    {
        var r = Judge(Fact("A", "var r = 3; Assert.Equal(5, r);"));
        Assert.Contains(r, x => x.Kind == FailureKinds.CharTestNoAssert && x.TestName == "A");
    }

    [Fact]
    public void Env_DateTimeNow_Rejected()
    {
        var r = Judge(Fact("A", "var d = DateTime.Now; var r = sut.CalculateRefundAsync(1); Assert.Equal(d, r);"));
        Assert.Contains(r, x => x.Kind == FailureKinds.CharTestEnvDependent);
    }

    [Fact]
    public void Env_OtherTokens_Rejected()
    {
        foreach (var token in new[] { "new Random()", "Guid.NewGuid()", "System.IO.File.ReadAllText(\"a\")", "Environment.MachineName" })
        {
            var r = Judge(Fact("A", $"var x = {token}; var r = sut.CalculateRefundAsync(1); Assert.Equal(x, r);"));
            Assert.Contains(r, y => y.Kind == FailureKinds.CharTestEnvDependent);
        }
    }

    [Fact]
    public void Env_InStringLiteral_Ignored()
    {
        var src = Fact("A", "// DateTime.Now\nvar r = sut.CalculateRefundAsync(1); Assert.Equal(\"DateTime.Now File. HttpClient\", r);");
        Assert.Empty(Judge(src));
    }

    [Fact]
    public void Env_PropertyNamedFile_Ignored() =>
        Assert.Empty(Judge(Fact("A", "var r = sut.CalculateRefundAsync(1); Assert.Equal(order.File.Name, r);")));

    [Fact]
    public void Valid_MsTest_ExpectedException_Ok()
    {
        var fw = TestFrameworks.Info(TestFramework.MsTest);
        var src = "[TestMethod]\n[ExpectedException(typeof(System.InvalidOperationException))]\npublic void A() { sut.CalculateRefundAsync(1); }";
        Assert.Empty(Judge(src, fw));
    }

    [Fact]
    public void Valid_NUnit_TestCase_Ok()
    {
        var fw = TestFrameworks.Info(TestFramework.NUnit);
        var src = "[TestCase(1)]\npublic void A(int a) { var r = sut.CalculateRefundAsync(a); Assert.That(r, Is.EqualTo(5)); }";
        Assert.Empty(Judge(src, fw));
    }
}
