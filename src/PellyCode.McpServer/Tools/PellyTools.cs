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
     Description("Lists the places that call, implement or override one C# method or property. Give path plus line (or name). One call per member.")]
    public async Task<string> Impact(
        [Description("C# file path, relative to the workspace root or absolute")] string path,
        [Description("1-based line inside the method or property")] int? line = null,
        [Description("Member name when line is not given, e.g. CancelAsync or OrderService.CancelAsync")] string? name = null,
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
