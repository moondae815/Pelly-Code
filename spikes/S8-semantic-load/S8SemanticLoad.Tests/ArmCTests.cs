using System.Diagnostics;
using S8SemanticLoad;

public class ArmCTests
{
    [Fact] public async Task Arm_c_scores_category_a_cases_by_name_and_by_symbol()
    {
        var sln = Path.Combine(TestPaths.Corpus, "Modern", "Modern.sln");
        using (var p = Process.Start(new ProcessStartInfo("dotnet", ["restore", sln]) { RedirectStandardOutput = true })!)
        {
            await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
        }
        var rows = await ArmC.RunAsync(TestPaths.RepoRoot, TestContext.Current.CancellationToken);

        Assert.Equal(["a1-cancel-returns-refund", "a2-update-status-returns-rows", "a3-rename-getorder"], rows.Select(r => r.CaseId).Order());
        foreach (var r in rows.Where(r => r.CaseId != "a3-rename-getorder"))
        {
            Assert.True(r.Measured, r.CaseId);
            Assert.NotNull(r.Semantic);
            Assert.NotNull(r.Syntactic);
        }
        // 레거시는 Visual Studio MSBuild가 있어야 의미 분석이 된다. 없으면 채점하지 않는다.
        var a3 = rows.Single(r => r.CaseId == "a3-rename-getorder");
        Assert.Equal(OperatingSystem.IsWindows(), a3.Measured);
        if (!a3.Measured) Assert.Null(a3.Semantic);
    }
}
