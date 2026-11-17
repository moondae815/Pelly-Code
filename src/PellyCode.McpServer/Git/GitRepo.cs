using System.Text;
using PellyCode.Core.Abstractions;

namespace PellyCode.McpServer.Git;

/// <summary>Read-only git access: HEAD, changed files and HEAD contents. Uses only status, rev-parse and show.</summary>
public sealed class GitRepo(IProcessRunner processes, string root)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The HEAD commit id, or null when there are no commits yet.</summary>
    public async Task<string?> HeadAsync(CancellationToken ct)
    {
        var r = await RunAsync(["rev-parse", "--verify", "-q", "HEAD"], ct);
        if (r.ExitCode != 0) return null;
        var sha = r.StdOut.Trim();
        return sha.Length == 0 ? null : sha;
    }

    /// <summary>Changed files against HEAD (staged, unstaged and untracked; ignored files are left out).</summary>
    public async Task<IReadOnlyList<GitChange>> ChangesAsync(CancellationToken ct)
    {
        var r = await RunAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"git status 실패 (종료 코드 {r.ExitCode}): {r.StdErr.Trim()}");

        var tokens = Encoding.UTF8.GetString(r.StdOutBytes).Split('\0');
        var list = new List<GitChange>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length < 4) continue;
            char x = t[0], y = t[1];
            var path = t[3..];
            if (x == 'R' || y == 'R' || x == 'C' || y == 'C')
            {
                var old = i + 1 < tokens.Length ? tokens[++i] : null;
                var kind = x == 'R' || y == 'R' ? GitChangeKind.Renamed : GitChangeKind.Added;
                list.Add(new GitChange(path, kind, kind == GitChangeKind.Renamed ? old : null));
                continue;
            }
            var k = x == '?' || x == 'A' || y == 'A' ? GitChangeKind.Added
                : x == 'D' || y == 'D' ? GitChangeKind.Deleted
                : GitChangeKind.Modified;
            list.Add(new GitChange(path, k, null));
        }
        return list.Where(c => !UnderPelly(c.Path) && (c.OldPath is null || !UnderPelly(c.OldPath))).ToList();
    }

    private static bool UnderPelly(string p) => p == ".pelly" || p.StartsWith(".pelly/", StringComparison.Ordinal);

    /// <summary>The raw bytes of <c>HEAD:path</c>, or null when HEAD has no such file.</summary>
    public async Task<byte[]?> ShowHeadAsync(string path, CancellationToken ct)
    {
        var r = await RunAsync(["show", $"HEAD:{path}"], ct);
        return r.ExitCode == 0 ? r.StdOutBytes : null;
    }

    private Task<ProcessResult> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var all = new List<string> { "-c", "core.quotepath=false" };
        all.AddRange(args);
        return processes.RunAsync(new ProcessSpec("git", all, root, Timeout), ct);
    }
}
