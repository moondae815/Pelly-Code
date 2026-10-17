using PellyCoder.Core.Builds;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.Builds;

public sealed class BaselineSnapshotTests
{
    private static Diagnostic Err(string file, int line, string code = "CS0103", string msg = "The name 'x' does not exist") =>
        new(file, line, 1, "error", code, msg);

    [Fact]
    public void New_IsMultisetDifference_IgnoringLines()
    {
        var baseline = new[] { Err("A.cs", 10) };
        var current = new[] { Err("A.cs", 12), Err("A.cs", 30) };

        var result = BaselineDiff.New(baseline, current);

        var only = Assert.Single(result);
        Assert.Equal(30, only.Line);
    }

    [Fact]
    public void New_FileCaseInsensitive()
    {
        var result = BaselineDiff.New([Err("src/A.cs", 1)], [Err("SRC/a.cs", 5)]);

        Assert.Empty(result);
    }

    [Fact]
    public void New_DifferentCodeOrMessage_IsNew()
    {
        var current = new[] { Err("A.cs", 1, "CS0103"), Err("A.cs", 2, "CS0117"), Err("A.cs", 3, msg: "other") };

        var result = BaselineDiff.New([Err("A.cs", 1)], current);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void New_KeepsCurrentOrder_AndEmptyBaselineReturnsAll()
    {
        var current = new[] { Err("B.cs", 9), Err("A.cs", 1) };

        Assert.Equal(current, BaselineDiff.New([], current));
    }
}
