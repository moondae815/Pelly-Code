using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PellyCode.McpServer.Tools;

[McpServerToolType]
public sealed class PellyTools(ServerState state)
{
    private const string NotImplemented = "아직 구현되지 않았습니다";

    [McpServerTool(Name = "pelly_impact"), Description("Lists where a C# or SQL symbol at the given path is used.")]
    public Task<string> Impact(
        [Description("File path relative to the workspace root")] string path,
        [Description("1-based line of the symbol")] int? line = null,
        [Description("Symbol name")] string? name = null) =>
        Task.FromResult(state.Context is null ? state.Error ?? NotImplemented : NotImplemented);

    [McpServerTool(Name = "pelly_verify"), Description("Checks the uncommitted changes against HEAD and reports new problems.")]
    public Task<string> Verify() =>
        Task.FromResult(state.Context is null ? state.Error ?? NotImplemented : NotImplemented);
}
