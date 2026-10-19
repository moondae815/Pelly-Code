using PellyCoder.Integration.Tests.Corpus;

namespace PellyCoder.Integration.Tests.Infrastructure;

internal static class CliBinary
{
    /// <summary>
    /// The pelly CLI built next to this test assembly's configuration (the project reference with
    /// <c>ReferenceOutputAssembly=false</c> makes the test build build it first, so it is never stale).
    /// </summary>
    public static string Dll()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tfm = baseDir.Name;
        var configuration = baseDir.Parent!.Name;
        var repo = Directory.GetParent(CorpusPaths.Root)!.Parent!.FullName;
        var dll = Path.Combine(repo, "src", "PellyCoder.Cli", "bin", configuration, tfm, "pelly.dll");
        Assert.True(File.Exists(dll), $"pelly CLI가 빌드되어 있지 않습니다: {dll}");
        return dll;
    }
}
