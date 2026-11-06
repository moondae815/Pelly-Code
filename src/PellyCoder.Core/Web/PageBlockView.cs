using System.Text;
using System.Text.RegularExpressions;
using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Web;

public enum PageServerCode { None, Output, Statements }

/// <summary>
/// One client block of an ASP/HTML page seen as plain JS or CSS text with the page's line count:
/// everything outside the block content is blanked, output segments become <c>__aspN__</c> names,
/// other server code becomes blanks.
/// </summary>
public sealed record PageBlockView(
    int BlockIndex, AspClientBlock Block, string Text, PageServerCode ServerCode, IReadOnlyList<string> ServerSegments)
{
    private static readonly Regex PlaceholderName = new(@"^__asp[0-9]+__$", RegexOptions.Compiled);

    public static bool IsPlaceholder(string name) => PlaceholderName.IsMatch(name);

    public static PageBlockView Build(AspPage page, string pageTextLf, int blockIndex)
    {
        var block = page.ClientBlocks[blockIndex];
        var lineStarts = LineStarts(pageTextLf);
        int OffsetOf(int line, int column) =>
            Math.Min(pageTextLf.Length, (line - 1 < lineStarts.Count ? lineStarts[line - 1] : pageTextLf.Length) + column - 1);

        var spans = new List<(int Start, int End, AspSegment Seg)>();
        foreach (var s in page.Segments)
        {
            if (s.Kind is AspSegmentKind.Html or AspSegmentKind.Metadata)
                continue;
            var start = OffsetOf(s.StartLine, s.StartColumn);
            var end = OffsetOf(s.EndLine, s.EndColumn) + 1;
            if (start >= block.ContentStart && end <= block.ContentEnd)
                spans.Add((start, end, s));
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var sb = new StringBuilder(pageTextLf.Length + 16);
        void Blank(int from, int to)
        {
            for (var i = from; i < to; i++)
                sb.Append(pageTextLf[i] == '\n' ? '\n' : ' ');
        }

        var serverCode = PageServerCode.None;
        var segments = new List<string>();
        var number = 0;
        Blank(0, block.ContentStart);
        var cursor = block.ContentStart;
        foreach (var (start, end, seg) in spans)
        {
            sb.Append(pageTextLf, cursor, start - cursor);
            segments.Add(pageTextLf[start..end]);
            if (seg.Kind == AspSegmentKind.Output)
            {
                // The name may be longer than a short segment, so columns shift; the line count stays whole.
                sb.Append($"__asp{number++}__");
                for (var i = start; i < end; i++)
                    if (pageTextLf[i] == '\n') sb.Append('\n');
                if (serverCode == PageServerCode.None)
                    serverCode = PageServerCode.Output;
            }
            else
            {
                Blank(start, end);
                serverCode = PageServerCode.Statements;
            }
            cursor = end;
        }
        sb.Append(pageTextLf, cursor, block.ContentEnd - cursor);
        Blank(block.ContentEnd, pageTextLf.Length);

        return new PageBlockView(blockIndex, block, BlankHtmlCommentWrappers(sb.ToString(), block), serverCode, segments);
    }

    // Blanked rather than left to the parser: the wrapper lines are legal JS only through the Annex B
    // HTML-like comments, and CSS has its own rules, so both sides see the same clean text.
    private static string BlankHtmlCommentWrappers(string view, AspClientBlock block)
    {
        var lines = view.Split('\n');
        for (var i = block.ContentStartLine - 1; i < block.ContentEndLine && i < lines.Length; i++)
            if (lines[i].Trim() is "<!--" or "//-->")
                lines[i] = new string(' ', lines[i].Length);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// True when the patched page has the same blocks (count, kind) and everything outside the target block's
    /// content is unchanged, so the open and close tags, the other blocks and the HTML are untouched.
    /// </summary>
    public static bool SameBlocks(AspPage original, AspPage patched, int targetIndex, string originalText, string patchedText)
    {
        if (original.ClientBlocks.Count != patched.ClientBlocks.Count
            || targetIndex < 0 || targetIndex >= original.ClientBlocks.Count)
            return false;
        for (var i = 0; i < original.ClientBlocks.Count; i++)
            if (original.ClientBlocks[i].Kind != patched.ClientBlocks[i].Kind
                || original.ClientBlocks[i].Closed != patched.ClientBlocks[i].Closed)
                return false;

        var o = original.ClientBlocks[targetIndex];
        var p = patched.ClientBlocks[targetIndex];
        return string.CompareOrdinal(originalText, 0, patchedText, 0, o.ContentStart) == 0
            && o.ContentStart == p.ContentStart
            && originalText.AsSpan(o.ContentEnd).SequenceEqual(patchedText.AsSpan(p.ContentEnd));
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                starts.Add(i + 1);
        return starts;
    }
}
