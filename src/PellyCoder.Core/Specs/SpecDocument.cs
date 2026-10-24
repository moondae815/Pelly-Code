using System.Text.RegularExpressions;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Specs;

/// <summary>
/// A change spec on disk. Updates rewrite single lines of the original text and write through
/// <see cref="FileGuard"/>, so encoding, BOM and newline style are kept and an outside edit is detected.
/// </summary>
public sealed class SpecDocument
{
    private static readonly Regex RunComment = new(@"\s*<!--\s*(?:run|failed):[^>]*?-->\s*$", RegexOptions.CultureInvariant);

    private readonly FileGuard _guard;

    private SpecDocument(FileGuard guard, FileSnapshot snapshot)
    {
        _guard = guard;
        Snapshot = snapshot;
        Spec = ChangeSpecParser.Parse(snapshot.Text);
    }

    public ChangeSpec Spec { get; private set; }

    public FileSnapshot Snapshot { get; private set; }

    public static SpecDocument Load(FileGuard guard, string path) => new(guard, guard.Read(path));

    public Task<FileWriteResult> MarkDoneAsync(string taskId, string runId, CancellationToken ct) =>
        RewriteTaskAsync([taskId], (line, _) => Compose(line, true, $"<!-- run: {runId} -->"), ct);

    public Task<FileWriteResult> MarkFailedAsync(string taskId, string runId, string failureKind, CancellationToken ct) =>
        RewriteTaskAsync([taskId], (line, _) => Compose(line, false, $"<!-- failed: {runId} {failureKind} -->"), ct);

    public Task<FileWriteResult> ClearMarksAsync(IEnumerable<string> taskIds, CancellationToken ct) =>
        RewriteTaskAsync(taskIds, (line, _) => Compose(line, false, null), ct);

    public Task<FileWriteResult> SetStatusAsync(SpecStatus status, CancellationToken ct)
    {
        string value = status switch
        {
            SpecStatus.Draft => "draft",
            SpecStatus.InProgress => "in-progress",
            _ => "done",
        };
        var lines = Snapshot.Text.Split('\n');
        int end = FrontMatterEnd(lines);
        for (int i = 1; i < end; i++)
        {
            // The parser's own rule, so every status line it reads can be rewritten.
            var (key, range) = ChangeSpecParser.FrontMatterField(lines[i]);
            if (key != "status") continue;
            var (offset, length) = range.GetOffsetAndLength(lines[i].Length);
            lines[i] = string.Concat(lines[i].AsSpan(0, offset), value, lines[i].AsSpan(offset + length));
            return WriteAsync(lines, ct);
        }
        throw new InvalidOperationException("The spec has no status line in its front matter.");
    }

    private Task<FileWriteResult> RewriteTaskAsync(IEnumerable<string> taskIds, Func<string, SpecTask, string> rewrite, CancellationToken ct)
    {
        var lines = Snapshot.Text.Split('\n');
        foreach (var id in taskIds)
        {
            var task = Spec.Tasks.FirstOrDefault(t => t.Id == id)
                ?? throw new ArgumentException($"Unknown task id: {id}", nameof(taskIds));
            lines[task.Line - 1] = rewrite(lines[task.Line - 1], task);
        }
        return WriteAsync(lines, ct);
    }

    private async Task<FileWriteResult> WriteAsync(string[] lines, CancellationToken ct)
    {
        var result = await _guard.WriteAsync(Snapshot, string.Join('\n', lines), ct);
        if (result.Status == FileWriteStatus.Written && result.Snapshot is { } written)
        {
            Snapshot = written;
            Spec = ChangeSpecParser.Parse(written.Text);
        }
        return result;
    }

    // Replaces the checkbox and the run/failed comment; every other character of the line stays.
    private static string Compose(string line, bool done, string? comment)
    {
        string body = RunComment.Replace(line, "");
        body = string.Concat("- [", done ? "x" : " ", "]", body.AsSpan(5));
        return comment is null ? body : body + " " + comment;
    }

    private static int FrontMatterEnd(string[] lines)
    {
        if (lines.Length > 0 && lines[0].TrimEnd() == "---")
            for (int i = 1; i < lines.Length; i++)
                if (lines[i].TrimEnd() == "---") return i;
        throw new InvalidOperationException("The spec has no front matter.");
    }
}
