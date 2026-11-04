using System.IO.Abstractions;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Asp;

public sealed record AspIncludedFile(string Path, AspPage Page);

/// <param name="Unresolved">Include paths that were missing or unreadable, relative to the site root with '/'.</param>
/// <param name="Truncated">True when the depth or file limit stopped the walk.</param>
public sealed record AspIncludeSet(IReadOnlyList<AspIncludedFile> Files, IReadOnlyList<string> Unresolved, bool Truncated);

/// <summary>Depth-first include resolution: each file once, cycles ignored, <c>file=</c> relative to the including file, <c>virtual=</c> from the site root.</summary>
public static class AspIncludeResolver
{
    public static AspIncludeSet Resolve(
        IFileSystem fs, string filePath, AspPage page, string siteRoot, AspTextCodec codec, int maxDepth = 5, int maxFiles = 40)
    {
        var state = new State(fs, siteRoot, codec, maxDepth, maxFiles);
        state.Visited.Add(Full(fs, filePath));
        state.Walk(filePath, page, 1);
        return new AspIncludeSet(state.Files, state.Unresolved, state.Truncated);
    }

    private static string Full(IFileSystem fs, string path) => fs.Path.GetFullPath(path.Replace('\\', '/'));

    private sealed class State(IFileSystem fs, string siteRoot, AspTextCodec codec, int maxDepth, int maxFiles)
    {
        public HashSet<string> Visited { get; } = new(PathComparers.ForOs);
        public List<AspIncludedFile> Files { get; } = [];
        public List<string> Unresolved { get; } = [];
        public bool Truncated { get; private set; }

        public void Walk(string currentPath, AspPage current, int depth)
        {
            foreach (var include in current.Includes)
            {
                var raw = include.Path.Trim().Replace('\\', '/');
                var target = include.Virtual
                    ? fs.Path.GetFullPath(fs.Path.Combine(Full(fs, siteRoot), raw.TrimStart('/')))
                    : fs.Path.GetFullPath(raw, fs.Path.GetDirectoryName(Full(fs, currentPath)) ?? "/");
                if (!Visited.Add(target)) continue;

                if (depth > maxDepth || Files.Count >= maxFiles) { Truncated = true; continue; }

                AspPage? parsed = null;
                try
                {
                    if (fs.File.Exists(target))
                        parsed = AspPage.Parse(codec.Decode(target, fs.File.ReadAllBytes(target)).Text);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                }

                if (parsed is null) { Unresolved.Add(Display(target, raw)); continue; }
                Files.Add(new AspIncludedFile(target, parsed));
                Walk(target, parsed, depth + 1);
            }
        }

        private string Display(string target, string raw)
        {
            var rel = fs.Path.GetRelativePath(Full(fs, siteRoot), target).Replace('\\', '/');
            return rel.StartsWith("..", StringComparison.Ordinal) || fs.Path.IsPathRooted(rel) ? raw : rel;
        }
    }
}
