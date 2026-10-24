using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Rejects an edit that leaves the target as it was in the original file (M3a T14): a no-op block (REPLACE equals SEARCH),
/// a reformat, or a later attempt that undoes an earlier one. Compared with the original, never the previous attempt, and
/// whitespace-insensitive with the benchmark judge's rule (whitespace runs, line breaks included, collapse to one space).
/// A commented-out change still differs and is not caught here. Whitespace inside string literals is collapsed too, so an
/// edit that only changes spaces in a literal (e.g. a message) is reported as no change — an accepted limitation.
/// </summary>
public static partial class NoChangeGuard
{
    /// <summary>
    /// True when the member <paramref name="targetDisplay"/> reads the same in both texts. False when it is missing from
    /// either text (the shape guard reports that case).
    /// </summary>
    public static bool CSharpUnchanged(string originalLf, string patchedLf, string targetDisplay, LanguageVersion version)
    {
        var before = MemberText(originalLf, targetDisplay, version);
        var after = MemberText(patchedLf, targetDisplay, version);
        return before is not null && after is not null && Same(before, after);
    }

    /// <summary>
    /// True when the member <paramref name="display"/> reads the same in both texts, or is in neither; false when it was
    /// added, removed or changed.
    /// </summary>
    public static bool CSharpMemberSame(string originalLf, string patchedLf, string display, LanguageVersion version)
    {
        var before = MemberText(originalLf, display, version);
        var after = MemberText(patchedLf, display, version);
        return before is null ? after is null : after is not null && Same(before, after);
    }

    /// <summary>True when batch <paramref name="targetIndex"/> of the patched file reads the same as in the original script.</summary>
    public static bool SqlUnchanged(SqlScript original, int targetIndex, string patchedLf, int compatLevel)
    {
        var patched = SqlBatchSplitter.Split(patchedLf, compatLevel);
        if (targetIndex < 0 || targetIndex >= original.Batches.Count || patched.Batches.Count != original.Batches.Count) return false;
        return Same(original.Batches[targetIndex].Text, patched.Batches[targetIndex].Text);
    }

    /// <summary>Whitespace runs collapsed to one space, trimmed: the benchmark judge's "same target" rule.</summary>
    public static string Normalize(string text) => WhitespaceRun().Replace(text, " ").Trim();

    private static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string? MemberText(string sourceLf, string display, LanguageVersion version)
    {
        var found = MemberLocator.AllMembers(sourceLf, version).Where(m => m.Display == display).ToList();
        if (found.Count != 1) return null;
        var lines = sourceLf.Split('\n');
        var span = found[0].Span;
        if (span.StartLine < 1 || span.EndLine > lines.Length || span.StartLine > span.EndLine) return null;
        return string.Join('\n', lines[(span.StartLine - 1)..span.EndLine]);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
