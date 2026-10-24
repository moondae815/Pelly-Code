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

public class DiagnosticAttributionTests
{
    // 최종 리뷰 I4: 웹 사이트 프로젝트는 .sln에 폴더 경로로 들어온다. 빈 이름이 모든 메시지와 일치하면 안 된다.
    [Fact] public void Web_site_entry_does_not_swallow_csharp_failures()
    {
        var census = new ProjectCensus([
            new CensusEntry("/s/App/App.csproj", ProjectKind.SdkCSharp, true),
            new CensusEntry("/s/WebSite1/", ProjectKind.NonCSharp, true),
        ]);
        Assert.True(LoadRunner.IsFromCSharp("Msbuild failed when processing the file '/s/App/App.csproj' with message: x", census));
        Assert.False(LoadRunner.IsFromCSharp("Cannot open project '/s/WebSite1/' because ...", census));
    }
}

public class SingleProjectLoadTests
{
    // 최종 리뷰 I3: 단일 csproj를 열면 ProjectReference도 함께 불러와진다. 인구 조사 1개가 다 불러와졌으면 통과다.
    [Fact] public async Task Single_csproj_with_project_reference_loads()
    {
        var csproj = Path.Combine(TestPaths.Corpus, "Modern", "Shop.Api.Tests", "Shop.Api.Tests.csproj");
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", ["restore", csproj]) { RedirectStandardOutput = true })!)
        {
            await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
        }
        var r = await LoadRunner.RunOnceAsync(csproj, new LoadOptions(false, false, "dev"), TestContext.Current.CancellationToken);
        Assert.Equal((1, 1), (r.Census.Loaded, r.Census.Extra));
        Assert.True(Verdict.Judge([r]).Single(j => j.Item == Verdict.LoadItem).Pass);
    }
}

public class PeakMemoryTests
{
    // macOS는 PeakWorkingSet64를 0으로 돌려준다. 그때는 현재 working set으로 대신한다.
    [Fact] public void Peak_memory_is_never_zero() => Assert.True(LoadRunner.PeakMemoryBytes() > 0);
}
