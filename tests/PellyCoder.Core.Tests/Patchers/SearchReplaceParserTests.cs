using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;

namespace PellyCoder.Core.Tests.Patchers;

public class SearchReplaceParserTests
{
    private static string Block(string s, string r) => $"<<<<<<< SEARCH\n{s}\n=======\n{r}\n>>>>>>> REPLACE\n";

    [Fact]
    public void Parse_SingleBlock()
    {
        var result = SearchReplaceParser.Parse(Block("a\nb", "c"));
        Assert.Null(result.Failure);
        var block = Assert.Single(result.Blocks);
        Assert.Equal("a\nb", block.Search);
        Assert.Equal("c", block.Replace);
    }

    [Fact]
    public void Parse_MultipleBlocks_InOrder()
    {
        var result = SearchReplaceParser.Parse(Block("a", "b") + "\n" + Block("c", "d"));
        Assert.Equal(["a", "c"], result.Blocks.Select(b => b.Search));
        Assert.Equal(["b", "d"], result.Blocks.Select(b => b.Replace));
    }

    [Fact]
    public void Parse_IgnoresProseAndFences()
    {
        var output = "Here is the fix:\n```csharp\n" + Block("a", "b") + "```\nThis changes a to b.\n";
        var result = SearchReplaceParser.Parse(output);
        var block = Assert.Single(result.Blocks);
        Assert.DoesNotContain("```", block.Search);
        Assert.DoesNotContain("```", block.Replace);
    }

    [Fact]
    public void Parse_MarkerWithTrailingSpaces_IsAccepted()
    {
        var result = SearchReplaceParser.Parse("<<<<<<< SEARCH  \na\n=======   \nb\n>>>>>>> REPLACE \t\n");
        Assert.Null(result.Failure);
        Assert.Single(result.Blocks);
    }

    [Fact]
    public void Parse_MissingReplaceMarker_IsParseFailure()
    {
        var result = SearchReplaceParser.Parse("<<<<<<< SEARCH\na\n=======\nb\n");
        Assert.Equal(PatchFailureKinds.Parse, result.Failure!.Kind);
        Assert.Contains("truncated", result.Failure.Detail);
    }

    [Fact]
    public void Parse_EmptySearch_IsParseFailure()
    {
        var result = SearchReplaceParser.Parse("<<<<<<< SEARCH\n=======\nb\n>>>>>>> REPLACE\n");
        Assert.Equal(PatchFailureKinds.Parse, result.Failure!.Kind);
    }

    [Fact]
    public void Parse_NoBlocks_IsParseFailure()
    {
        var result = SearchReplaceParser.Parse("just prose");
        Assert.Equal(PatchFailureKinds.Parse, result.Failure!.Kind);
    }

    [Fact]
    public void Parse_EmptyReplace_IsAllowed()
    {
        var result = SearchReplaceParser.Parse("<<<<<<< SEARCH\na\n=======\n>>>>>>> REPLACE\n");
        Assert.Null(result.Failure);
        Assert.Equal("", Assert.Single(result.Blocks).Replace);
    }
}
