using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;

namespace PellyCoder.Core.Tests.Patchers;

public class SearchReplacePatcherTests
{
    // 1..3 usings, 4..9 read-only, 10..30 target.
    private static string Original()
    {
        var lines = new List<string> { "using A;", "using B;", "using C;" };
        for (var i = 4; i <= 9; i++) lines.Add($"// readonly {i}");
        lines.Add("void Target()");
        lines.Add("{");
        lines.Add("            var x = 1;");
        lines.Add("            var y = 2;  ");
        for (var i = 14; i <= 29; i++) lines.Add($"    // filler {i}");
        lines.Add("}");
        return string.Join('\n', lines) + "\n";
    }

    private static readonly EditableRegion[] Regions =
    [
        new(new TextSpanInfo(10, 30), "target"),
        new(new TextSpanInfo(1, 3), "usings"),
    ];

    private static PatchOutcome Run(string original, params PatchBlock[] blocks) =>
        SearchReplacePatcher.Apply(original, blocks, Regions);

    [Fact]
    public void Exact_Applies_StageExact()
    {
        var o = Run(Original(), new PatchBlock("            var x = 1;", "            var x = 5;"));
        Assert.True(o.Success);
        Assert.Equal([MatchStage.Exact], o.Stages);
        Assert.Contains("var x = 5;", o.NewText);
        Assert.EndsWith("}\n", o.NewText);
    }

    [Fact]
    public void TrailingWhitespaceDiff_Applies_StageTrailingWhitespace()
    {
        var o = Run(Original(), new PatchBlock("            var y = 2;", "            var y = 3;"));
        Assert.True(o.Success);
        Assert.Equal([MatchStage.TrailingWhitespace], o.Stages);
        Assert.Contains("var y = 3;", o.NewText);
    }

    [Fact]
    public void IndentationOff_Applies_AndReindentsReplace()
    {
        var o = Run(Original(), new PatchBlock("  var x = 1;", "  if (a)\n    var x = 2;\n\n  var z;"));
        Assert.True(o.Success);
        Assert.Equal([MatchStage.Indentation], o.Stages);
        var text = o.NewText!;
        Assert.Contains("\n            if (a)\n              var x = 2;\n\n            var z;\n", text);
    }

    [Fact]
    public void IndentationWithTabs_UsesTabs()
    {
        var original = "class C\n{\n\t\tint a;\n\t\tint b;\n}\n";
        var regions = new[] { new EditableRegion(new TextSpanInfo(1, 5), "all") };
        var o = SearchReplacePatcher.Apply(original, [new PatchBlock("int a;", "int a;\nint c;")], regions);
        Assert.True(o.Success);
        Assert.Equal("class C\n{\n\t\tint a;\n\t\tint c;\n\t\tint b;\n}\n", o.NewText);
    }

    [Fact]
    public void TwoMatchesInRegion_IsAmbiguous()
    {
        var original = "x\nsame\nsame\ny\n";
        var regions = new[] { new EditableRegion(new TextSpanInfo(1, 4), "all") };
        var o = SearchReplacePatcher.Apply(original, [new PatchBlock("same", "other")], regions);
        Assert.False(o.Success);
        Assert.Equal(PatchFailureKinds.Ambiguous, o.Failure!.Kind);
        Assert.Contains("2", o.Failure.Detail);
        Assert.Null(o.NewText);
    }

    [Fact]
    public void MatchOnlyOutsideRegion_IsOutsideRegion()
    {
        var o = Run(Original(), new PatchBlock("// readonly 5", "// changed"));
        Assert.False(o.Success);
        Assert.Equal(PatchFailureKinds.OutsideRegion, o.Failure!.Kind);
        Assert.Contains("1", o.Failure.Detail);
    }

    [Fact]
    public void NoMatch_IsNotFound_WithNearestOriginalPlusMinus3()
    {
        var o = Run(Original(), new PatchBlock("            var q = 1;", "x"));
        Assert.False(o.Success);
        Assert.Equal(PatchFailureKinds.NotFound, o.Failure!.Kind);
        var nearest = o.Failure.NearestOriginal!.Split('\n');
        Assert.Equal(7, nearest.Length);
        Assert.Equal("            var x = 1;", nearest[3]);
    }

    [Fact]
    public void MultipleBlocks_AppliedSequentially_RegionShifts()
    {
        var o = Run(Original(),
            new PatchBlock("using C;", "using C;\nusing D;\nusing E;"),
            new PatchBlock("            var x = 1;", "            var x = 9;"));
        Assert.True(o.Success);
        Assert.Equal([MatchStage.Exact, MatchStage.Exact], o.Stages);
        Assert.Contains("using E;", o.NewText);
        Assert.Contains("var x = 9;", o.NewText);
    }

    [Fact]
    public void OverlappingBlocks_IsOverlap()
    {
        var o = Run(Original(),
            new PatchBlock("            var x = 1;", "            var x = 2;"),
            new PatchBlock("            var x = 2;", "            var x = 3;"));
        Assert.False(o.Success);
        Assert.Equal(PatchFailureKinds.Overlap, o.Failure!.Kind);
    }

    [Fact]
    public void EmptyLinesMatchOnlyEmptyLines_InIndentationStage()
    {
        var original = "a\n\nb\nc\n  \nb\n";
        var regions = new[] { new EditableRegion(new TextSpanInfo(1, 6), "all") };
        // "   a / <empty> / b" matches only lines 1-3; line 5 is whitespace-only (trims empty) but is not followed by a match here.
        var o = SearchReplacePatcher.Apply(original, [new PatchBlock(" a\n\n b", "Z")], regions);
        Assert.True(o.Success);
        Assert.Equal("Z\nc\n  \nb\n", o.NewText);

        var miss = SearchReplacePatcher.Apply(original, [new PatchBlock(" a\nb", "Z")], regions);
        Assert.Equal(PatchFailureKinds.NotFound, miss.Failure!.Kind);
    }

    [Fact]
    public void MarkerScanner_DetectsConflictMarkers()
    {
        Assert.True(MarkerScanner.HasConflictMarkers("a\n<<<<<<< HEAD\nb"));
        Assert.True(MarkerScanner.HasConflictMarkers("a\n=======\nb"));
        Assert.True(MarkerScanner.HasConflictMarkers(">>>>>>> x"));
        Assert.False(MarkerScanner.HasConflictMarkers("a\n  <<<<<<< not at start\nb"));
    }
}
