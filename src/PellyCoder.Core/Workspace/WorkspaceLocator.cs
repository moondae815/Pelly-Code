using System.IO.Abstractions;

namespace PellyCoder.Core.Workspaces;

public sealed record Workspace(string Root, string PellyDir);

public static class WorkspaceLocator
{
    /// <summary>
    /// Root is the nearest ancestor folder holding .git, .svn or a *.sln file;
    /// otherwise the target file's own folder (design 8.1).
    /// </summary>
    public static Workspace Resolve(IFileSystem fs, string targetFilePath)
    {
        var fileDir = fs.Path.GetDirectoryName(fs.Path.GetFullPath(targetFilePath)) ?? fs.Path.GetPathRoot(targetFilePath)!;
        var root = fileDir;
        for (var dir = fileDir; dir is not null; dir = fs.Path.GetDirectoryName(dir))
        {
            if (IsRoot(fs, dir)) { root = dir; break; }
        }
        return new Workspace(root, fs.Path.Combine(root, ".pelly"));
    }

    public static void EnsurePellyDir(IFileSystem fs, Workspace ws)
    {
        fs.Directory.CreateDirectory(fs.Path.Combine(ws.PellyDir, "runs"));
        fs.Directory.CreateDirectory(fs.Path.Combine(ws.PellyDir, "backup"));
        var ignore = fs.Path.Combine(ws.PellyDir, ".gitignore");
        if (!fs.File.Exists(ignore)) fs.File.WriteAllText(ignore, "*\n");
    }

    private static bool IsRoot(IFileSystem fs, string dir) =>
        fs.Directory.Exists(fs.Path.Combine(dir, ".git"))
        || fs.File.Exists(fs.Path.Combine(dir, ".git")) // git worktrees keep .git as a file
        || fs.Directory.Exists(fs.Path.Combine(dir, ".svn"))
        || fs.Directory.EnumerateFiles(dir, "*.sln").Any();
}
