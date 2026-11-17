using System.ComponentModel;
using System.IO.Abstractions;
using ModelContextProtocol.Server;
using PellyCode.Core.Impact;
using PellyCode.McpServer.Impact;

namespace PellyCode.McpServer.Tools;

[McpServerToolType]
public sealed class PellyTools(ServerState state, IFileSystem fs, ISemanticLoader loader)
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
            return await new ImpactService(fs, ctx, loader).FindAsync(path, line, name, ct);
        }
        finally
        {
            ctx.ToolLock.Release();
        }
    }

    [McpServerTool(Name = "pelly_verify"), Description("Checks the uncommitted changes against HEAD and reports new problems.")]
    public Task<string> Verify() =>
        Task.FromResult(state.Context is null ? state.Error ?? NotImplemented : NotImplemented);
}
