using PellyCoder.Cli.Terminal;

namespace PellyCoder.Cli.Tests;

public class DiffRendererTests
{
    [Fact]
    public void Diff_UnifiedLines_ContextAndMarkers()
    {
        var before = string.Join("\n", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j");
        var after = string.Join("\n", "a", "b", "c", "d", "E", "f", "g", "h", "i", "j");

        var lines = DiffRenderer.UnifiedLines(before, after);

        Assert.Equal(
            new (char, string)[]
            {
                ('@', "@@ -2,7 +2,7 @@"),
                (' ', "b"), (' ', "c"), (' ', "d"),
                ('-', "e"), ('+', "E"),
                (' ', "f"), (' ', "g"), (' ', "h"),
            },
            lines.ToArray());
    }

    [Fact]
    public void Diff_Identical_YieldsNoLines()
    {
        Assert.Empty(DiffRenderer.UnifiedLines("a\nb", "a\nb"));
    }

    [Fact]
    public void Diff_DistantChanges_MakeSeparateHunks()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"l{i}"));
        var after = before.Replace("l2\n", "X\n").Replace("l19\n", "Y\n");

        var headers = DiffRenderer.UnifiedLines(before, after).Where(l => l.Kind == '@').ToArray();

        Assert.Equal(2, headers.Length);
    }

    [Fact]
    public void Diff_InsertionOnly_HeaderCountsMatch()
    {
        var lines = DiffRenderer.UnifiedLines("a\nb", "a\nx\nb", context: 1);

        Assert.Equal(('@', "@@ -1,2 +1,3 @@"), lines[0]);
        Assert.Contains(('+', "x"), lines);
    }
}
