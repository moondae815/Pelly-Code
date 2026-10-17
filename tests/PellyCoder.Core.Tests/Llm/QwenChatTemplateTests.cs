using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Llm;

namespace PellyCoder.Core.Tests.Llm;

public sealed class QwenChatTemplateTests
{
    [Fact]
    public void Build_MatchesS1ChatMl()
    {
        var t = QwenChatTemplate.LoadDefault();
        Assert.Equal(
            "<|im_start|>system\nS<|im_end|>\n<|im_start|>user\nU<|im_end|>\n<|im_start|>assistant\n",
            t.Build("S", "U"));
    }

    [Fact]
    public void EmptyThink_IsS3Prefill()
    {
        Assert.Equal("<think>\n\n</think>\n\n", QwenChatTemplate.LoadDefault().EmptyThink);
    }

    [Fact]
    public void LoadFromFile_OverridesDefault()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/t.json", new MockFileData("""
            {"systemStart":"[S]","systemEnd":"[/S]","userStart":"[U]","userEnd":"[/U]","assistantStart":"[A]",
             "thinkOpen":"<t>","thinkClose":"</t>","endOfTurn":"[E]"}
            """));
        var t = QwenChatTemplate.LoadFromFile(fs, "/t.json");
        Assert.Equal("[S]a[/S][U]b[/U][A]", t.Build("a", "b"));
        Assert.Equal("<t></t>", t.EmptyThink);
        Assert.Equal("[E]", t.EndOfTurn);
    }
}
