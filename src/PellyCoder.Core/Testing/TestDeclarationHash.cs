using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Testing;

/// <summary>
/// Hash of a named test's method declarations (texts without trivia), independent of the rest of the declaring files. A run
/// and its caller (<c>pelly apply</c>, the benchmark) compare it to tell whether the test itself changed.
/// </summary>
public static class TestDeclarationHash
{
    /// <summary>
    /// SHA-256 hex of the declarations <see cref="TestMethodIndex.Find"/> finds for <paramref name="testName"/>, in full-name
    /// order; null when there is none (or a declaring file can no longer be read).
    /// </summary>
    public static string? Of(IFileSystem fs, string workspaceRoot, string testName)
    {
        var declarations = TestMethodIndex.Find(fs, workspaceRoot, testName).DistinctBy(d => (d.FullName, d.File)).ToList();
        if (declarations.Count == 0) return null;
        var texts = new List<string>();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var d in declarations)
            {
                if (!sources.TryGetValue(d.File, out var source)) sources[d.File] = source = fs.File.ReadAllText(d.File);
                texts.AddRange(TestMethodIndex.DeclarationTexts(source, [d.FullName]));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return OfTexts(texts);
    }

    /// <summary>The hash rule shared with <c>TestFirstState.HashOf</c>: the declaration texts joined by newlines, UTF-8, SHA-256 hex.</summary>
    internal static string OfTexts(IEnumerable<string> declarations) =>
        TextFileCodec.HashHex(Encoding.UTF8.GetBytes(string.Join("\n", declarations)));
}
