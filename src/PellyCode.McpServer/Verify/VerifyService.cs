using System.IO.Abstractions;
using PellyCode.Core.Abstractions;
using PellyCode.Core.Specs;
using PellyCode.McpServer.Baseline;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Verify;

/// <summary>
/// <c>pelly_verify</c> (design §7): the changed files, then the C#, SQL and ASP·JS·CSS checks, then <see cref="VerifyReport.Render"/>.
/// Waits for the HEAD baseline without the tool lock and takes the lock for the live build, the tests and the SQL and web checks
/// (they compare each changed file with its own HEAD text and need no baseline). A cancelled call
/// cancels the live build or test (the process runner kills the process tree); the baseline work goes on.
/// </summary>
/// <param name="sqlExecutors">Opens the SQL executor for the (allow-listed, non-production) <c>sql.connectionString</c>.</param>
/// <param name="processes">Runs cscript for the VBScript syntax check.</param>
/// <param name="isWindows">cscript exists only on Windows.</param>
public sealed class VerifyService(IFileSystem fs, ServerContext ctx, GitRepo git, BaselineService baseline, IGroupVerifier verifier,
    IBaselineTestRunner tests, TimeSpan waitLimit, Func<string, ISqlExecutor> sqlExecutors, IProcessRunner processes, Func<bool> isWindows)
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

    private readonly CsVerifier _cs = new(fs, ctx, baseline.MirrorDir, verifier, tests,
        p => SqlVerifier.IsSql(p) || WebVerifier.IsWeb(ctx.Options.Asp.Extensions, p));
    private readonly SqlVerifier _sql = new(fs, ctx, sqlExecutors);
    private readonly WebVerifier _web = new(fs, ctx, processes, isWindows);

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

        // A changed file no check looks at is named on a skip line; when that is every file, nothing was checked at all.
        var notChecked = changes.Where(c => !Checked(c)).Select(c => c.Path).ToList();
        if (notChecked.Count == changes.Count) return VerifyReport.RenderNothingChecked(notChecked);

        var report = new VerifyReport();
        var scope = _cs.Scope(changes, report);
        Baseline.Baseline? head = null;
        if (scope is not null)
        {
            var wait = progress is null ? FallbackWaitLimit : waitLimit;
            head = await baseline.GetAsync(scope.BaselineProjects, wait, progress, ct);
            if (head.Status == BaselineStatus.Building) return StillBuilding;
        }

        // Files a check skipped as a whole (no SQL connection, no asp·web settings) were not checked either.
        var skipped = new HashSet<string>(notChecked, StringComparer.Ordinal);
        await ctx.ToolLock.WaitAsync(ct);
        try
        {
            if (scope is not null) await _cs.CheckAsync(scope, head!, report, progress, ct);
            // SQL and web compare each changed file with its own HEAD text: no baseline build needed.
            skipped.UnionWith(await _sql.CheckAsync(changes, git.ShowHeadAsync, report, progress, ct));
            skipped.UnionWith(await _web.CheckAsync(changes, git.ShowHeadAsync, report, progress, ct));
        }
        finally
        {
            ctx.ToolLock.Release();
        }
        report.Unchecked(notChecked);
        return report.Render(nothingChecked: changes.All(c => skipped.Contains(c.Path)));
    }

    /// <summary>
    /// True when a check looks at the change: the C# build (a file of a project, or a build file of the solution) by its path or
    /// old path, or the SQL or web check (not for a deleted file).
    /// </summary>
    private bool Checked(GitChange c) =>
        new[] { c.Path, c.OldPath }.OfType<string>().Any(p => _cs.OwningProjects(p).Count > 0)
        || (c.Kind != GitChangeKind.Deleted && (SqlVerifier.IsSql(c.Path) || _web.IsWeb(c.Path)));
}
