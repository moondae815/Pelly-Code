using System.ComponentModel;
using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PellyCode.Core.Impact;
using PellyCode.McpServer.Impact;
using PellyCode.McpServer.Verify;

namespace PellyCode.McpServer.Tools;

[McpServerToolType]
public sealed class PellyTools(ServerState state, IFileSystem fs, ISemanticLoader loader, IServiceProvider services, ILogger<PellyTools> logger)
{
    private const string NotImplemented = "아직 구현되지 않았습니다";

    [McpServerTool(Name = "pelly_impact"),
     Description("Lists the places that use one thing you plan to change: a C# method or property (.cs), a stored procedure (.sql: ASP pages and C# strings that call it), or a JS function (.js, or a <script> in an .asp page). Give path plus line (or name). One call per target.")]
    public async Task<string> Impact(
        [Description("File path (.cs, .sql, .js, .asp), relative to the workspace root or absolute")] string path,
        [Description("1-based line inside the method, procedure or function")] int? line = null,
        [Description("Name when line is not given, e.g. OrderService.CancelAsync, dbo.usp_CancelOrder, fnSearch. For .sql, give neither to check every procedure in the file")] string? name = null,
        CancellationToken ct = default)
    {
        if (state.Context is not { } ctx) return state.Error ?? NotImplemented;
        await ctx.ToolLock.WaitAsync(ct);
        try
        {
            return await Guarded(() => new ImpactService(fs, ctx, loader).FindAsync(path, line, name, ct));
        }
        finally
        {
            ctx.ToolLock.Release();
        }
    }

    [McpServerTool(Name = "pelly_verify"),
     Description("Call this after editing code and before you finish. No arguments. Checks the files changed since the last commit (HEAD) and lists only NEW problems: C# build errors and failing tests of the changed projects (any file of a project, and build files such as Directory.Build.props), SQL compile errors (.sql), and ASP, JS and CSS checks (.asp/.inc, .js, .css, pages that include a changed file). Changed files no check covers are listed as skipped. Errors must be fixed; warnings are hints to check. Fix what it lists, then call it again. If it says the baseline is still building, call it again.")]
    public async Task<string> Verify(RequestContext<CallToolRequestParams> request, IProgress<ProgressNotificationValue> progress,
        CancellationToken ct = default)
    {
        if (state.Context is null) return state.Error ?? NotImplemented;
        if (services.GetService<VerifyService>() is not { } verify) return NotImplemented;
        // No progress token from the client: nothing keeps the call alive, so the shorter wait applies.
        if (request.Params?.ProgressToken is null) return await Guarded(() => verify.VerifyAsync(null, ct));
        await using var beat = new ProgressHeartbeat(progress, TimeProvider.System, VerifyService.ProgressInterval);
        beat.Report("변경 사항을 확인하는 중입니다");
        return await Guarded(() => verify.VerifyAsync(beat, ct));
    }

    /// <summary>
    /// Runs a tool body; an unexpected exception (not a cancellation) is logged and comes back as 「검사 중 오류가 났습니다: …」
    /// instead of the SDK's generic tool error.
    /// </summary>
    internal async Task<string> Guarded(Func<Task<string>> run)
    {
        try
        {
            return await run();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "도구 실행 중 예상하지 못한 오류");
            return $"검사 중 오류가 났습니다: {e.Message}";
        }
    }
}
