namespace PellyCoder.Core.Tests;

/// <summary>Finds the repository's eval/corpus folder by walking up from the test binaries.</summary>
internal static class CorpusLocation
{
    private static readonly string Root = Find();

    public static string Get(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "eval", "corpus");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("eval/corpus folder not found.");
    }
}
