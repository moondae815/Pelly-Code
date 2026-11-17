namespace PellyCode.McpServer;

/// <summary>
/// The server's own options. Everything else on the command line goes to the generic host. deploy/mcp-server.md and the MCP part of
/// deploy/field-checklist.md may only use these (DocsTests).
/// </summary>
public static class CommandLine
{
    public const string Root = "--root";
    public const string LogFile = "--log-file";
    public const string Check = "--check";

    public static readonly IReadOnlyList<string> Options = [Root, LogFile, Check];

    /// <summary>The value after <paramref name="name"/>, or null when the option is absent or last.</summary>
    public static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
