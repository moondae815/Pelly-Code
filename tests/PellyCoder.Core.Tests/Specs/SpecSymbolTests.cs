using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Tests.Specs;

public sealed class SpecSymbolTests
{
    [Theory]
    [InlineData("Cancel", null, "Cancel")]
    [InlineData("OrderService.Cancel", "OrderService", "Cancel")]
    [InlineData("Outer.Inner.M", "Inner", "M")]
    public void Split_takes_last_segment_as_member_and_the_one_before_as_class(string symbol, string? cls, string member)
    {
        Assert.Equal((cls, member), SpecSymbol.Split(symbol));
    }
}
