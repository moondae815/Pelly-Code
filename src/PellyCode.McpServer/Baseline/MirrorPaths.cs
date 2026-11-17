using System.Security.Cryptography;
using System.Text;

namespace PellyCode.McpServer.Baseline;

/// <summary>Where the HEAD mirror of a workspace lives: outside the workspace, one folder per root.</summary>
public static class MirrorPaths
{
    public static string For(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PellyCode", "mcp-base", hash);
    }
}
