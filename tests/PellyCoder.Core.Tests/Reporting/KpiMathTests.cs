using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Tests.Reporting;

public sealed class KpiMathTests
{
    [Theory]
    [InlineData(0.5, 5)]
    [InlineData(0.9, 9)]
    [InlineData(1.0, 10)]
    public void Percentile_NearestRank(double p, double expected) =>
        Assert.Equal(expected, KpiMath.Percentile([10, 3, 7, 1, 9, 2, 8, 4, 6, 5], p));

    [Fact]
    public void Percentile_SingleValue() => Assert.Equal(3, KpiMath.Percentile([3], 0.9));

    [Fact]
    public void Percentile_Empty_IsNull() => Assert.Null(KpiMath.Percentile([], 0.5));

    [Fact]
    public void ParseSuccess_SumsAttemptsMinusFormatErrors() =>
        Assert.Equal((3, 4), KpiMath.ParseSuccess([(3, 1), (1, 0)]));
}
