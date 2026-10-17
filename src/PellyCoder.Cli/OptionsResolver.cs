using System.IO.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Cli;

/// <summary>Merges config sources for a command (design 8.1): user file, workspace or --config file, environment, CLI flags.</summary>
public sealed class OptionsResolver(IFileSystem fs, ConfigSources sources)
{
    /// <param name="anchorPath">A path inside the workspace (the target file, or a placeholder in the current directory).</param>
    public OptionsLoadResult Resolve(string anchorPath, string? configPath, string? think, string? model, string? endpoint,
        bool strictBaseline = false, bool warnAsError = false)
    {
        if (configPath is not null && !fs.File.Exists(configPath))
            return new OptionsLoadResult(null, [$"config file not found: {configPath}"]);

        var workspaceConfig = configPath ?? fs.Path.Combine(WorkspaceLocator.Resolve(fs, anchorPath).Root, "pelly.json");
        var overrides = new Dictionary<string, string?>();
        if (think is not null) overrides["llm:thinkMode"] = think;
        if (model is not null) overrides["llm:model"] = model;
        if (endpoint is not null) overrides["llm:endpoint"] = endpoint;
        if (strictBaseline) overrides["build:strictBaseline"] = "true";
        if (warnAsError) overrides["build:warnAsError"] = "true";
        return PellyOptionsLoader.Load(fs, sources.UserConfigPath, workspaceConfig, sources.Environment, overrides);
    }
}
