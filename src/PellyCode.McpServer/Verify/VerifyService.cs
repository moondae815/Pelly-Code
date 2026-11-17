using System.IO.Abstractions;
using PellyCode.Core.Specs;
using PellyCode.McpServer.Baseline;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Verify;

/// <summary>
/// <c>pelly_verify</c> (design §7): the changed files, then the C# check (Task 10 adds SQL and web), then <see cref="VerifyReport.Render"/>.
/// Waits for the HEAD baseline without the tool lock and takes the lock only for the live build and tests. A cancelled call
/// cancels the live build or test (the process runner kills the process tree); the baseline work goes on.
/// </summary>
public sealed class VerifyService(IFileSystem fs, ServerContext ctx, GitRepo git, BaselineService baseline, IGroupVerifier verifier,
    IBaselineTestRunner tests, TimeSpan waitLimit)
{
    /// <summary>
    /// How long a call waits for the baseline when progress notifications go out (measured with OpenCode 2026-11-17: notifications
    /// every 10 s kept 900 s calls alive; the registration example's timeout is 660000).
    /// </summary>
    public static readonly TimeSpan DefaultWaitLimit = TimeSpan.FromSeconds(600);

    /// <summary>The wait when no progress can be sent: OpenCode cuts a call at 60 s without notifications, less 15 s.</summary>
    public static readonly TimeSpan FallbackWaitLimit = TimeSpan.FromSeconds(45);

    /// <summary>How often a progress notification goes out while the call runs.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    public const string NoChanges = "HEAD 이후 바뀐 파일이 없습니다";
    public const string StillBuilding = "기준 빌드가 아직 진행 중입니다. 잠시 뒤 다시 pelly_verify를 부르세요";

    private readonly CsVerifier _cs = new(fs, ctx, baseline.MirrorDir, verifier, tests);

    internal bool ToolLockFree => ctx.ToolLock.CurrentCount == 1;

    /// <param name="progress">Where progress goes; null when it cannot reach the client (then the wait is <see cref="FallbackWaitLimit"/>).</param>
    public async Task<string> VerifyAsync(IProgress<string>? progress, CancellationToken ct)
    {
        IReadOnlyList<GitChange> changes;
        try
        {
            changes = await git.ChangesAsync(ct);
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
        // Build outputs of the live tree are not changes (a repository without a .gitignore for bin/obj).
        changes = changes.Where(c => !BaseMirror.IsExcluded(c.Path) || (c.OldPath is { } old && !BaseMirror.IsExcluded(old))).ToList();
        if (changes.Count == 0) return NoChanges;

        var report = new VerifyReport();
        if (_cs.Scope(changes, report) is { } scope)
        {
            var wait = progress is null ? FallbackWaitLimit : waitLimit;
            var head = await baseline.GetAsync(scope.BaselineProjects, wait, progress, ct);
            if (head.Status == BaselineStatus.Building) return StillBuilding;

            await ctx.ToolLock.WaitAsync(ct);
            try
            {
                await _cs.CheckAsync(scope, head, report, progress, ct);
            }
            finally
            {
                ctx.ToolLock.Release();
            }
        }
        return report.Render();
    }
}
