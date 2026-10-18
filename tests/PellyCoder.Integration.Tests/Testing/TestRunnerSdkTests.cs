using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Testing;
using PellyCoder.Integration.Tests.Builds;

namespace PellyCoder.Integration.Tests.Testing;

public class TestRunnerSdkTests
{
    [Fact]
    public async Task Sdk_RealRun_ModernApiTests()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = CorpusCopy.Create("Modern");
        var root = RealPath(created);
        try
        {
            // The corpus test project is a Microsoft.Testing.Platform (xunit v3) project; .NET 10 needs the global.json opt-in.
            await File.WriteAllTextAsync(Path.Combine(root, "global.json"), """{"test":{"runner":"Microsoft.Testing.Platform"}}""", ct);
            // Relative restore path and a link-free root on purpose: through the macOS /var -> /private/var link the restore
            // skips the transitive ProjectReference and the built test app misses Shop.Api.dll.
            var processes = new SystemProcessRunner(TimeProvider.System);
            var csproj = Path.Combine(root, "Shop.Api.Tests", "Shop.Api.Tests.csproj");
            var restore = await processes.RunAsync(new ProcessSpec("dotnet", ["restore", Path.Combine("Shop.Api.Tests", "Shop.Api.Tests.csproj"), "--nologo"], root, TimeSpan.FromMinutes(5)), ct);
            Assert.True(restore.ExitCode == 0, restore.StdOut + restore.StdErr);

            var trx = Path.Combine(root, ".pelly", "test.trx");
            var run = await new TestRunner(processes, new BuildOptions(), new TestOptions())
                .RunAsync(new BuildTool(BuildToolKind.Dotnet, "dotnet"), csproj, root, trx, ct);

            Assert.Null(run.Error);
            Assert.Empty(run.BuildErrors);
            Assert.True(run.ExitCode == 0, run.ExitCode + " " + string.Join(";", run.Results.Select(r => r.FullName + r.Passed + r.Message)));
            Assert.True(run.PerTestResults);
            Assert.True(run.Results.Count >= 3, "results: " + run.Results.Count);
            Assert.All(run.Results, r => Assert.True(r.Passed, r.FullName + ": " + r.Message));
        }
        finally
        {
            CorpusCopy.Delete(created);
        }
    }

    /// <summary>The path with every directory link resolved (macOS temp folders live under the /var link).</summary>
    private static string RealPath(string path)
    {
        var current = Path.GetPathRoot(path)!;
        foreach (var segment in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target) current = target.FullName;
        }
        return current;
    }
}
