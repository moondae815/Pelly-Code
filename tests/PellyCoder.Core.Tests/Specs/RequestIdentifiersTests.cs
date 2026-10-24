using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Tests.Specs;

public sealed class RequestIdentifiersTests
{
    [Fact]
    public void Extract_takes_backticks_pascal_underscore_and_schema_names()
    {
        var ids = RequestIdentifiers.Extract("`OrderStatus.Shipped`이면 InvalidOperationException을 던지고 _context 대신 dbo.usp_X 호출, CancelTime 컬럼 추가");
        Assert.Contains("OrderStatus.Shipped", ids);
        Assert.Contains("InvalidOperationException", ids);
        Assert.Contains("_context", ids);
        Assert.Contains("dbo.usp_X", ids);
        Assert.Contains("CancelTime", ids);
        Assert.DoesNotContain("이면", ids);
        Assert.DoesNotContain("컬럼", ids);
    }

    [Fact]
    public void Extract_skips_korean_and_pure_numbers_but_keeps_two_letter_pascal()
    {
        var ids = RequestIdentifiers.Extract("Ok 값을 12 로 바꾼다 A x1 100");
        Assert.Contains("Ok", ids);
        Assert.DoesNotContain("12", ids);
        Assert.DoesNotContain("100", ids);
        Assert.DoesNotContain("A", ids);
        Assert.DoesNotContain("x1", ids);
    }

    [Fact]
    public void Extract_returns_each_word_once()
    {
        Assert.Single(RequestIdentifiers.Extract("Foo Foo `Foo`"));
    }
}
