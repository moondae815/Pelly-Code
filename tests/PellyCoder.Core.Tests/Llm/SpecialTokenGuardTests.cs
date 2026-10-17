using PellyCoder.Core.Llm;

namespace PellyCoder.Core.Tests.Llm;

public sealed class SpecialTokenGuardTests
{
    private static readonly IReadOnlyCollection<string> Tokens = QwenTokenCounter.Shared.SpecialTokens;

    [Fact]
    public void SpecialTokens_MatchTokenizerMeta()
    {
        Assert.Equal(26, Tokens.Count);
        Assert.Contains("<|im_end|>", Tokens);
    }

    [Fact]
    public void FindIn_DetectsImEnd() =>
        Assert.Equal("<|im_end|>", SpecialTokenGuard.FindIn("var s = \"<|im_end|>\";", Tokens));

    [Fact]
    public void FindIn_DetectsThinkClose() =>
        Assert.Equal("</think>", SpecialTokenGuard.FindIn("// done </think>", Tokens));

    [Fact]
    public void FindIn_PlainText_IsNull() =>
        Assert.Null(SpecialTokenGuard.FindIn("return a < b || c |> d;", Tokens));
}
