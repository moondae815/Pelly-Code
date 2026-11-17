using System.IO.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using PellyCode.Core.Files;
using PellyCode.Core.Impact;

namespace PellyCode.McpServer.Impact;

/// <summary>
/// Keeps the loaded <see cref="Solution"/> of each project bundle for the life of the server (design §6). On a repeat
/// call only the .cs files whose write time changed get new text (<c>WithDocumentText</c>); a changed .csproj of the
/// bundle reloads it. The returned load has no owner, so the caller's <c>using</c> leaves the cached workspace alive.
/// Not thread-safe: the tools run one at a time under <see cref="ServerContext.ToolLock"/>.
/// </summary>
public sealed class CachingSemanticLoader(ISemanticLoader inner, IFileSystem fs) : ISemanticLoader
{
    /// <summary>Bundles kept at once; past this the least recently used one is released.</summary>
    private const int MaxEntries = 3;

    private sealed class Entry(Solution solution, IDisposable owner, Dictionary<string, DateTime> projects, Dictionary<DocumentId, DateTime> documents)
    {
        public Solution Solution { get; set; } = solution;
        public IDisposable Owner { get; } = owner;
        public Dictionary<string, DateTime> Projects { get; } = projects;
        public Dictionary<DocumentId, DateTime> Documents { get; } = documents;
    }

    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();

    public async Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct)
    {
        var full = projects.Select(p => fs.Path.GetFullPath(p)).ToList();
        var key = string.Join('\n', full);
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.Projects.All(p => WriteTime(p.Key) == p.Value))
            {
                Touch(key);
                entry.Solution = Refresh(entry);
                return new SemanticLoad(entry.Solution, null, null);
            }
            Evict(key);
        }

        var load = await inner.LoadAsync(projects, ct);
        if (load.Failure is not null || load.Solution is null) return load;

        var solution = load.Solution;
        var documents = new Dictionary<DocumentId, DateTime>();
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
            if (document.FilePath is not null) documents[document.Id] = WriteTime(document.FilePath);
        var projectTimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var p in full) projectTimes[p] = WriteTime(p);
        // The load is not disposed here: it now belongs to the cache, and Evict disposes it.
        _cache[key] = new Entry(solution, load, projectTimes, documents);
        Touch(key);
        while (_recent.Count > MaxEntries) Evict(_recent.Last!.Value);
        return new SemanticLoad(solution, null, null);
    }

    /// <summary>The cached solution with every changed or deleted document file brought up to date.</summary>
    private Solution Refresh(Entry entry)
    {
        var solution = entry.Solution;
        foreach (var (id, seen) in entry.Documents.ToList())
        {
            var path = solution.GetDocument(id)?.FilePath;
            if (path is null) continue;
            var now = WriteTime(path);
            if (now == seen) continue;
            if (now == DateTime.MinValue)
            {
                solution = solution.RemoveDocument(id);
                entry.Documents.Remove(id);
                continue;
            }

            string text;
            try { text = TextDecoding.Decode(fs.File.ReadAllBytes(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            solution = solution.WithDocumentText(id, SourceText.From(text), PreservationMode.PreserveIdentity);
            entry.Documents[id] = now;
        }
        return solution;
    }

    /// <summary>Last write time (UTC), or <see cref="DateTime.MinValue"/> when the file is gone or unreadable.</summary>
    private DateTime WriteTime(string path)
    {
        try { return fs.File.Exists(path) ? fs.File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private void Touch(string key)
    {
        _recent.Remove(key);
        _recent.AddFirst(key);
    }

    private void Evict(string key)
    {
        if (_cache.Remove(key, out var entry)) entry.Owner.Dispose();
        _recent.Remove(key);
    }
}
