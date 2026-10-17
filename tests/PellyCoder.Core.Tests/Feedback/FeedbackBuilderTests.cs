using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.Feedbacks;

public sealed class FeedbackBuilderTests
{
    private static readonly QwenTokenCounter Counter = QwenTokenCounter.Shared;

    private static string FileOf(int lines) =>
        string.Join("\n", Enumerable.Range(1, lines).Select(i => $"line{i}"));

    [Fact]
    public void Diagnostics_FormatWithPlusMinus3AndMarker()
    {
        var text = FileOf(50);
        var errs = new[] { new Diagnostic("OrderService.cs", 41, 5, "error", "CS1002", "; expected") };
        var fb = new FeedbackBuilder(Counter, 296).ForDiagnostics("SYNTAX CHECK FAILED", errs, text);
        var expected = string.Join("\n",
            "SYNTAX CHECK FAILED (1 error)",
            "OrderService.cs(41): CS1002 ; expected",
            "  38 | line38",
            "  39 | line39",
            "  40 | line40",
            "> 41 | line41",
            "  42 | line42",
            "  43 | line43",
            "  44 | line44");
        Assert.Equal(expected, fb.Text);
        Assert.Equal(Counter.Count(expected), fb.Tokens);
    }

    [Fact]
    public void Diagnostics_RightAlignsNumbersAndClampsToFile()
    {
        var text = FileOf(10);
        var errs = new[] { new Diagnostic("A.cs", 9, 1, "error", "CS1002", "x") };
        var fb = new FeedbackBuilder(Counter, 296).ForDiagnostics("H", errs, text);
        Assert.Equal(string.Join("\n",
            "H (1 error)",
            "A.cs(9): CS1002 x",
            "   6 | line6",
            "   7 | line7",
            "   8 | line8",
            ">  9 | line9",
            "  10 | line10"), fb.Text);
    }

    [Fact]
    public void Diagnostics_DuplicatesAppearOnce_SeparatedByBlankLine()
    {
        var text = FileOf(30);
        var a = new Diagnostic("A.cs", 5, 1, "error", "CS1002", "m");
        var b = new Diagnostic("A.cs", 20, 1, "error", "CS1002", "m");
        var fb = new FeedbackBuilder(Counter, 296).ForDiagnostics("H", new[] { a, a, b }, text);
        Assert.StartsWith("H (2 errors)\n", fb.Text);
        Assert.Contains("\n\nA.cs(20): CS1002 m\n", fb.Text);
        Assert.Equal(1, fb.Text.Split("A.cs(5):").Length - 1);
    }

    [Fact]
    public void Diagnostics_OverBudget_DropsTrailingErrors()
    {
        var text = FileOf(200);
        var errs = Enumerable.Range(1, 10)
            .Select(i => new Diagnostic("A.cs", i * 15, 1, "error", "CS1002", "; expected")).ToArray();
        var fb = new FeedbackBuilder(Counter, 296).ForDiagnostics("SYNTAX CHECK FAILED", errs, text);
        Assert.True(fb.Tokens <= 296);
        Assert.Equal(Counter.Count(fb.Text), fb.Tokens);
        Assert.StartsWith("SYNTAX CHECK FAILED (10 errors)\nA.cs(15): CS1002", fb.Text);
        Assert.DoesNotContain("A.cs(150)", fb.Text);
    }

    [Fact]
    public void Diagnostics_SingleHugeError_IsTruncated()
    {
        var text = FileOf(20);
        var msg = string.Join(" ", Enumerable.Repeat("verylongmessage", 400));
        var errs = new[] { new Diagnostic("A.cs", 10, 1, "error", "CS1002", msg) };
        var fb = new FeedbackBuilder(Counter, 296).ForDiagnostics("H", errs, text);
        Assert.True(fb.Tokens <= 296);
        Assert.Contains("…", fb.Text);
        Assert.StartsWith("H (1 error)\nA.cs(10): CS1002 verylongmessage", fb.Text);
    }

    [Theory]
    [InlineData("NotFound", "SEARCH NOT FOUND: the SEARCH block does not match the [EDITABLE] text exactly. Nearest original lines:\nNEAR")]
    [InlineData("Ambiguous", "SEARCH AMBIGUOUS: the SEARCH block matches more than one place. Include more surrounding lines.")]
    [InlineData("OutsideRegion", "SEARCH OUTSIDE EDITABLE REGION: copy SEARCH lines only from [EDITABLE: target] or [EDITABLE: usings].")]
    [InlineData("Overlap", "BLOCKS OVERLAP: each SEARCH must refer to original lines not changed by an earlier block.")]
    [InlineData("Parse", "FORMAT ERROR: DETAIL. Output only SEARCH/REPLACE blocks.")]
    [InlineData("Unencodable", "ENCODING ERROR: DETAIL. The file encoding cannot store this character; use only characters it supports.")]
    public void PatchFailure_UsesExactPhrase(string kind, string expected)
    {
        var fb = new FeedbackBuilder(Counter, 296).ForPatchFailure(new PatchFailure(kind, "DETAIL", "NEAR"));
        Assert.Equal(expected, fb.Text);
        Assert.Equal(Counter.Count(fb.Text), fb.Tokens);
    }

    [Fact]
    public void PatchFailure_NotFoundWithoutNearest_OmitsSuffix()
    {
        var fb = new FeedbackBuilder(Counter, 296).ForPatchFailure(new PatchFailure("NotFound", "", null));
        Assert.Equal("SEARCH NOT FOUND: the SEARCH block does not match the [EDITABLE] text exactly.", fb.Text);
    }

    [Fact]
    public void PatchFailure_OverBudget_TruncatesNearestFromEnd()
    {
        var near = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"    var value{i} = Compute({i});"));
        var fb = new FeedbackBuilder(Counter, 296).ForPatchFailure(new PatchFailure("NotFound", "", near));
        Assert.True(fb.Tokens <= 296);
        Assert.StartsWith("SEARCH NOT FOUND: the SEARCH block does not match the [EDITABLE] text exactly. Nearest original lines:\n    var value1 = ", fb.Text);
    }

    [Fact]
    public void PatchFailure_UnknownKind_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FeedbackBuilder(Counter, 296).ForPatchFailure(new PatchFailure("Bogus", "", null)));
}
