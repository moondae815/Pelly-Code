namespace PellyCoder.Core.Tests.Impact;

internal static class ImpactFixture
{
    /// <summary>The checked-in SameName solution folder (found by walking up from the test binaries).</summary>
    public static string SameName()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "tests", "fixtures", "impact", "SameName");
            if (Directory.Exists(p)) return p;
        }

        throw new DirectoryNotFoundException("tests/fixtures/impact/SameName");
    }
}
