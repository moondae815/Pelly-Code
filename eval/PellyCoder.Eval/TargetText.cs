using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Sql;

namespace PellyCoder.Eval;

/// <summary>
/// The judged range of a file (README "대상 범위"): the member named by <c>method</c>, the member around <c>line</c>, or the
/// SQL object batch. Texts are LF-normalised. Null when the target cannot be found (or is ambiguous).
/// </summary>
public static class TargetText
{
    /// <param name="originalDisplay">
    /// For a <c>line</c> task's final file: the display of the member found at <paramref name="line"/> in the original
    /// (<see cref="DisplayAtLine"/>). An edit shifts lines, so the final member is matched by display, not by line.
    /// </param>
    public static string? ForCs(string sourceLf, string? method, int? line, string? originalDisplay = null)
    {
        MemberCandidate? member;
        if (method is not null)
        {
            var found = MemberLocator.FindByName(sourceLf, LanguageVersion.Latest, null, method);
            member = found.Count == 1 ? found[0] : null;
        }
        else if (originalDisplay is not null)
        {
            var found = MemberLocator.AllMembers(sourceLf, LanguageVersion.Latest).Where(m => m.Display == originalDisplay).ToList();
            member = found.Count == 1 ? found[0] : null;
        }
        else if (line is { } l)
        {
            member = MemberLocator.FindAtLine(sourceLf, LanguageVersion.Latest, l);
        }
        else
        {
            return null;
        }

        if (member is null) return null;
        var lines = sourceLf.Split('\n');
        return string.Join('\n', lines[(member.Span.StartLine - 1)..member.Span.EndLine]);
    }

    /// <summary>Display (<c>Type.Member(params)</c>) of the member around the 1-based <paramref name="line"/>.</summary>
    public static string? DisplayAtLine(string sourceLf, int line) =>
        MemberLocator.FindAtLine(sourceLf, LanguageVersion.Latest, line)?.Display;

    /// <summary>The batch of <paramref name="objectName"/>, or the only object batch when no name is given.</summary>
    public static string? ForSql(string sourceLf, string? objectName, int compatLevel) => SqlTarget(sourceLf, objectName, compatLevel)?.Batch.Text;

    internal static (SqlScript Script, SqlBatch Batch)? SqlTarget(string sourceLf, string? objectName, int compatLevel)
    {
        var script = SqlBatchSplitter.Split(sourceLf, compatLevel);
        List<SqlBatch> found;
        if (objectName is not null)
        {
            var name = SqlObjectName.Parse(objectName);
            found = [.. script.ObjectBatches.Where(b => b.ObjectName is { } n && n.SameAs(name))];
        }
        else
        {
            found = [.. script.ObjectBatches];
        }
        return found.Count == 1 ? (script, found[0]) : null;
    }
}
