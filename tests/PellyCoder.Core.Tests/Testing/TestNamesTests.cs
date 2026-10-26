using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.Testing;

public class TestNamesTests
{
    [Fact]
    public void Matches_DotBoundarySuffix() =>
        Assert.True(TestNames.Matches("OrderServiceTests.Cancel", "Shop.Tests.OrderServiceTests.Cancel"));

    [Fact]
    public void Matches_RejectsPartialSegment() =>
        Assert.False(TestNames.Matches("OrderServiceTests.Cancel", "Shop.Tests.XOrderServiceTests.Cancel"));

    [Fact]
    public void Matches_RejectsPrefix() =>
        Assert.False(TestNames.Matches("Cancel", "Shop.T.Cancel_WhenShipped"));

    [Fact]
    public void Matches_IgnoresArguments() =>
        Assert.True(TestNames.Matches("T.M", "N.T.M(x: 1, y: \"a\")"));

    [Fact]
    public void Matches_IsCaseSensitive() =>
        Assert.False(TestNames.Matches("t.m", "N.T.M"));

    [Fact]
    public void Matches_ExactName() =>
        Assert.True(TestNames.Matches("N.T.M", "N.T.M"));

    [Fact]
    public void WithoutArguments_CutsFromFirstParenAndTrims() =>
        Assert.Equal("N.T.M", TestNames.WithoutArguments("  N.T.M (x: 1) "));

    [Fact]
    public void IsValid_RejectsSpacesAndParentheses()
    {
        Assert.False(TestNames.IsValid("T.M(1)"));
        Assert.False(TestNames.IsValid("T M"));
        Assert.True(TestNames.IsValid("_a.B1"));
    }

    [Fact]
    public void IsValid_RejectsTrailingNewlineAndEmptySegments()
    {
        Assert.False(TestNames.IsValid("A.B\n"));
        Assert.False(TestNames.IsValid("A..B"));
        Assert.False(TestNames.IsValid("1A"));
        Assert.False(TestNames.IsValid(""));
    }
}
