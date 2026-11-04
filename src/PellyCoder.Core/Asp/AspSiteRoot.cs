using System.IO.Abstractions;

namespace PellyCoder.Core.Asp;

/// <summary>Where <c>include virtual=</c> paths resolve from: <c>asp.siteRoot</c> (relative to the workspace root) or the workspace root.</summary>
public static class AspSiteRoot
{
    public static string Resolve(IFileSystem fs, string? configured, string workspaceRoot) =>
        configured is { Length: > 0 }
            ? fs.Path.GetFullPath(fs.Path.IsPathRooted(configured) ? configured : fs.Path.Combine(workspaceRoot, configured))
            : workspaceRoot;
}
