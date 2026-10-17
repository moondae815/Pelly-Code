using PellyCoder.Core.Models;

namespace PellyCoder.Core.Patchers;

public sealed record ParseResult(IReadOnlyList<PatchBlock> Blocks, PatchFailure? Failure);

public static class SearchReplaceParser
{
    private const string SearchMarker = "<<<<<<< SEARCH";
    private const string DividerMarker = "=======";
    private const string ReplaceMarker = ">>>>>>> REPLACE";

    private enum State { Outside, InSearch, InReplace }

    public static ParseResult Parse(string modelOutput)
    {
        var lines = modelOutput.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<PatchBlock>();
        var state = State.Outside;
        var search = new List<string>();
        var replace = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            switch (state)
            {
                case State.Outside:
                    if (line == SearchMarker)
                    {
                        search.Clear();
                        replace.Clear();
                        state = State.InSearch;
                    }
                    break;
                case State.InSearch:
                    if (line == DividerMarker) state = State.InReplace;
                    else if (line == SearchMarker || line == ReplaceMarker)
                    {
                        return Fail($"Block {blocks.Count + 1}: unexpected '{line}' marker inside the SEARCH section (a marker is missing or misplaced).");
                    }
                    else search.Add(raw);
                    break;
                case State.InReplace:
                    if (line == ReplaceMarker)
                    {
                        var searchText = string.Join('\n', search);
                        if (string.IsNullOrWhiteSpace(searchText))
                        {
                            return Fail($"Block {blocks.Count + 1}: SEARCH section is empty.");
                        }

                        blocks.Add(new PatchBlock(searchText, string.Join('\n', replace)));
                        state = State.Outside;
                    }
                    else if (line == SearchMarker || line == DividerMarker)
                    {
                        return Fail($"Block {blocks.Count + 1}: unexpected '{line}' marker inside the REPLACE section (a marker is missing or misplaced).");
                    }
                    else replace.Add(raw);
                    break;
            }
        }

        if (state != State.Outside)
        {
            return Fail($"Block {blocks.Count + 1}: output truncated, missing '{(state == State.InSearch ? DividerMarker : ReplaceMarker)}' marker.");
        }

        if (blocks.Count == 0) return Fail("No SEARCH/REPLACE blocks found in the model output.");
        return new ParseResult(blocks, null);
    }

    private static ParseResult Fail(string detail) =>
        new([], new PatchFailure(PatchFailureKinds.Parse, detail, null));
}
