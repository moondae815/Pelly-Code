using S8SemanticLoad;

public class LoadReportTests
{
    private static LoadReport WithNames(string mode) => ReportFx.Clean(mode: mode,
        messages: ["Msbuild failed when processing /src/Shop.Web/Shop.Web.csproj"],
        projects: [new ProjectHealth("Shop.Data", ProjectKind.LegacyCSharp, true, 0, 0)],
        a3: new A3Result(["legacy/shop.data/iorderdao.cs#GetOrder"], [], []));

    [Fact] public void Field_mode_json_has_no_names()
    {
        var json = WithNames("field").ToJson();
        foreach (var s in new[] { "Shop.", ".cs", "/", "\\\\", "GetOrder" })
            Assert.DoesNotContain(s, json);
        Assert.Contains("Shop.", WithNames("detail").ToJson());
    }

    [Fact] public void Json_round_trips()
    {
        var r = ReportFx.Clean(rate: 0.97, errorsExcluded: 3);
        var back = LoadReport.FromJson(r.ToJson());
        Assert.Equal((r.Calls, r.Health, r.Census, r.OpenSeconds), (back.Calls, back.Health, back.Census, back.OpenSeconds));
    }

    [Fact] public void Summary_marks_msbuild_version_as_guess() =>
        Assert.Contains("추정", (ReportFx.Clean() with { MsBuildGuess = "18.0" }).ToSummary());
}
