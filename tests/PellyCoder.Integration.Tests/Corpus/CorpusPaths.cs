namespace PellyCoder.Integration.Tests.Corpus;

/// <summary>저장소의 eval/corpus 경로를 찾는다.</summary>
public static class CorpusPaths
{
    public static string Root { get; } = Find();

    public static string Get(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "eval", "corpus");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("eval/corpus 폴더를 찾을 수 없습니다.");
    }
}
