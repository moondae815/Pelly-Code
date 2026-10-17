namespace PellyCoder.Integration.Tests.Builds;

/// <summary>Copies a corpus project into a temp folder so builds never touch eval/corpus.</summary>
internal static class CorpusCopy
{
    public static string Create(string corpusRelative, bool includeBuildOutput = false)
    {
        var source = Corpus.CorpusPaths.Get(corpusRelative);
        var target = Path.Combine(Path.GetTempPath(), "pelly-build-" + Guid.NewGuid().ToString("N")[..8]);
        Copy(new DirectoryInfo(source), new DirectoryInfo(target), includeBuildOutput);
        // Returned as given (macOS: /var/..., a link to /private/var): BuildVerifier also matches the resolved path.
        return target;
    }

    public static void Delete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { /* best effort: MSBuild node reuse may still hold files */ }
        catch (UnauthorizedAccessException) { }
    }

    private static void Copy(DirectoryInfo from, DirectoryInfo to, bool includeBuildOutput)
    {
        to.Create();
        foreach (var f in from.GetFiles()) f.CopyTo(Path.Combine(to.FullName, f.Name));
        foreach (var d in from.GetDirectories())
        {
            if (!includeBuildOutput && d.Name is "bin" or "obj" or "TestResults") continue;
            Copy(d, new DirectoryInfo(Path.Combine(to.FullName, d.Name)), includeBuildOutput);
        }
    }
}
