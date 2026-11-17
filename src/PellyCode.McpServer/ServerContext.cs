using System.IO.Abstractions;
using PellyCode.Core.Config;
using PellyCode.Core.Workspaces;

namespace PellyCode.McpServer;

/// <summary>What the tools share: the workspace root, the loaded options and the lock that serializes tool runs.</summary>
public sealed class ServerContext(string root, PellyOptions options, Workspace ws)
{
    public string Root { get; } = root;
    public PellyOptions Options { get; } = options;
    public Workspace Workspace { get; } = ws;
    public SemaphoreSlim ToolLock { get; } = new(1, 1);

    /// <summary>Same source order as the Cli OptionsResolver: user config, &lt;root&gt;/pelly.json, environment.</summary>
    public static ServerContext? TryCreate(IFileSystem fs, string root, out string? error)
    {
        root = fs.Path.GetFullPath(root);
        var git = fs.Path.Combine(root, ".git");
        if (!fs.Directory.Exists(git) && !fs.File.Exists(git))
        {
            error = $"git 저장소가 아닙니다: {root}";
            return null;
        }

        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = e.Value as string;

        var loaded = PellyOptionsLoader.Load(fs, PellyPaths.UserConfigPath(), fs.Path.Combine(root, "pelly.json"),
            env, new Dictionary<string, string?>());
        if (loaded.Options is null)
        {
            error = string.Join(Environment.NewLine, loaded.Errors);
            return null;
        }

        error = null;
        return new ServerContext(root, loaded.Options, new Workspace(root, fs.Path.Combine(root, ".pelly")));
    }
}

/// <summary>Startup result handed to the tools: a context, or the message to show when there is none.</summary>
public sealed record ServerState(ServerContext? Context, string? Error);
