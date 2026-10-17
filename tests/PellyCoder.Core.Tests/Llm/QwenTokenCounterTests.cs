using System.Text.Json;
using PellyCoder.Core.Llm;

namespace PellyCoder.Core.Tests.Llm;

public sealed class QwenTokenCounterTests
{
    private sealed record Sample(string Text, int ServerCount);

    private static string FindSamplesPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PellyCoder.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", "tokenizer", "samples.json");
    }

    [Fact]
    public void Count_MatchesServerPromptEvalCount_ForRecordedSamples()
    {
        var samples = JsonSerializer.Deserialize<List<Sample>>(
            File.ReadAllText(FindSamplesPath()),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(8, samples.Count);
        foreach (var s in samples)
            Assert.Equal(s.ServerCount, QwenTokenCounter.Shared.Count(s.Text));
    }

    [Fact]
    public void SpecialTokens_CountAsOne()
    {
        Assert.Equal(1, QwenTokenCounter.Shared.Count("<|im_start|>"));
        Assert.Equal(1, QwenTokenCounter.Shared.Count("</think>"));
    }

    [Fact]
    public void Count_Empty_IsZero() => Assert.Equal(0, QwenTokenCounter.Shared.Count(""));

    [Fact]
    public void Shared_IsSameInstance() => Assert.Same(QwenTokenCounter.Shared, QwenTokenCounter.Shared);
}
