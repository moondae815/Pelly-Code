using System.Diagnostics;
using S8SemanticLoad;

public class LoadRunnerTests
{
    private static readonly LoadOptions Opts = new(Detail: true, CheckA3: false, ToolForm: "dev");

    [Fact] public async Task Modern_corpus_loads_clean_after_restore()
    {
        var sln = Path.Combine(TestPaths.Corpus, "Modern", "Modern.sln");
        using (var p = Process.Start(new ProcessStartInfo("dotnet", ["restore", sln]) { RedirectStandardOutput = true })!)
        {
            await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.Equal(0, p.ExitCode);
        }
        var r = await LoadRunner.RunOnceAsync(sln, Opts, TestContext.Current.CancellationToken);
        Assert.Equal(3, r.Census.Loaded);
        Assert.Equal(0, r.Health.CompileErrorsCounted);
        Assert.True(r.Calls.Rate >= 0.99, $"rate {r.Calls.Rate}");
        Assert.DoesNotContain(r.Issues, i => i.FromCSharpProject && i.Count > 0);
    }

    [Fact] public async Task Legacy_corpus_on_non_windows_reports_msbuild_not_found()
    {
        var r = await LoadRunner.RunOnceAsync(Path.Combine(TestPaths.Corpus, "Legacy", "Legacy.sln"), Opts, TestContext.Current.CancellationToken);
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(r.Issues, i => i.FromCSharpProject && i.Count > 0);
        else
            Assert.Contains(r.Issues, i => i.Kind == WorkspaceIssueKind.MsBuildNotFound);
    }
}

public class PeakMemoryTests
{
    // macOS는 PeakWorkingSet64를 0으로 돌려준다. 그때는 현재 working set으로 대신한다.
    [Fact] public void Peak_memory_is_never_zero() => Assert.True(LoadRunner.PeakMemoryBytes() > 0);
}
