using System.Text.Encodings.Web;
using System.Text.Json;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public sealed class ExportCommandTests : IDisposable
{
    private const string Secret = "SECRET-교환대금";
    private const string Path1 = "/Users/dev/erp";
    private const string Url = "http://10.1.2.3:11434";
    private static readonly string[] Markers = [Secret, Path1, Url, "10.1.2.3"];
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pelly-export-" + Guid.NewGuid().ToString("N"));

    public ExportCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string Dirty => $"{Secret} {Path1} {Url}";

    private static List<TaskResult> Rows() =>
    [
        new("cs-modern-001", "cs", "Failed", 1, 3, [$"사유 {Dirty}"], new VerificationResult("Failed", $"검증 상세 {Dirty}", "Passed"),
            "Passed", false, "Judge", 12.5, 0.3, 40, true, ["Applied", "Rejected"], null, false, 2),
        new("sql-001", "sql", "Skipped", null, 0, [], null, "NotRequired", false, null, null, null, 0, false, [],
            $"환경 오류(복원 실패): {Dirty}", false),
        new("sql-002", "sql", "Skipped", null, 0, [], null, "NotRequired", false, null, null, null, 0, false, [],
            $"SQL 연결이 없습니다 {Dirty}", true),
        new("cs-legacy-001", "cs", "Passed", 0, 1, [], VerificationResult.Pass, "Passed", false, null, 3, 0.1, 0, false, ["Applied"], null, true),
    ];

    private static string Header => $"* 실행: x · pelly 0.9.0 · 모델 `qwen3` ({Url}, Ollama 0.40.0) · {Path1}";

    private string MakeSingle(string name = "20261019-000000")
    {
        var dir = System.IO.Path.Combine(_dir, name);
        Directory.CreateDirectory(System.IO.Path.Combine(dir, "work", "x"));
        File.WriteAllText(System.IO.Path.Combine(dir, "work", "x", "f.txt"), Dirty);
        File.WriteAllText(System.IO.Path.Combine(dir, "results.json"), JsonSerializer.Serialize(Rows(), Json));
        File.WriteAllText(System.IO.Path.Combine(dir, "report.md"), KpiReport.Render(Rows(), Header));
        return dir;
    }

    private string MakeRepeat(string name = "20261019-111111")
    {
        var dir = System.IO.Path.Combine(_dir, name);
        var runs = new List<IReadOnlyList<TaskResult>>();
        for (var k = 1; k <= 3; k++)
        {
            var run = System.IO.Path.Combine(dir, $"run-{k}");
            Directory.CreateDirectory(run);
            File.WriteAllText(System.IO.Path.Combine(run, "results.json"), JsonSerializer.Serialize(Rows(), Json));
            File.WriteAllText(System.IO.Path.Combine(run, "report.md"), KpiReport.Render(Rows(), Header));
            runs.Add(Rows());
        }
        var built = RepeatSummary.Build(runs);
        var summary = built with
        {
            Runs = built.Runs.Select(r => r with { Folder = $"run-{r.Run}" }).ToList(),
            Conditions = new RunConditions("qwen3", "p1", "p2", "Retry", 512, new(0.6, 0.95, 20, 0, 0), new(0.2, 0.9, 20, 0, 0), "0.9.0", "*", null, "0.40.0", "abc"),
        };
        File.WriteAllText(System.IO.Path.Combine(dir, "summary.json"), RepeatSummary.ToJson(summary));
        File.WriteAllText(System.IO.Path.Combine(dir, "summary.md"), KpiReport.RenderRepeat(summary, Header));
        return dir;
    }

    private (int Code, string Out, string Dest) Export(string input, string? dest = null)
    {
        dest ??= System.IO.Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N"));
        var parsed = EvalArguments.Parse(["export", input, "--out", dest]);
        Assert.Null(parsed.Error);
        var output = new StringWriter();
        return (ExportCommand.Run(parsed, output), output.ToString(), dest);
    }

    private static List<TaskResult> Read(string path) =>
        JsonSerializer.Deserialize<List<TaskResult>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void Export_NoMarkerInAnyOutputFile()
    {
        foreach (var input in new[] { MakeSingle(), MakeRepeat() })
        {
            var (code, _, dest) = Export(input);
            Assert.Equal(0, code);
            var files = Directory.GetFiles(dest, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
            foreach (var f in files)
            {
                var text = File.ReadAllText(f);
                foreach (var m in Markers) Assert.DoesNotContain(m, text);
            }
            Assert.False(Directory.Exists(System.IO.Path.Combine(dest, "work")));
        }
    }

    [Fact]
    public void Export_KeepsKpiFields_AndSanitizesEachDroppedField()
    {
        var input = MakeSingle();
        var (code, _, dest) = Export(input);
        Assert.Equal(0, code);
        var src = Rows();
        var got = Read(System.IO.Path.Combine(dest, "results.json"));
        Assert.Equal(src.Count, got.Count);
        for (var i = 0; i < src.Count; i++)
        {
            var (a, b) = (src[i], got[i]);
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Status, b.Status);
            Assert.Equal(a.ExitCode, b.ExitCode);
            Assert.Equal(a.Attempts, b.Attempts);
            Assert.Equal(a.FailureType, b.FailureType);
            Assert.Equal(a.BuildCheck, b.BuildCheck);
            Assert.Equal(a.InvariantViolated, b.InvariantViolated);
            Assert.Equal(a.WallSeconds, b.WallSeconds);
            Assert.Equal(a.TtftSeconds, b.TtftSeconds);
            Assert.Equal(a.ThinkTokens, b.ThinkTokens);
            Assert.Equal(a.ThinkTruncated, b.ThinkTruncated);
            Assert.Equal(a.EditOutcomes, b.EditOutcomes);
            Assert.Equal(a.IsLegacy, b.IsLegacy);
            Assert.Equal(a.FormatErrors, b.FormatErrors);
            Assert.Equal(a.Independent?.Status, b.Independent?.Status);
            Assert.Equal(a.Independent?.Build, b.Independent?.Build);
            Assert.Empty(b.Reasons);
            Assert.Null(b.Independent?.Detail);
        }
        Assert.Equal("환경 오류(복원 실패)", got[1].SkipReason);
        Assert.Equal("생략", got[2].SkipReason);
        Assert.Null(got[0].SkipReason);
        var report = File.ReadAllText(System.IO.Path.Combine(dest, "report.md"));
        Assert.Contains("* 반출본: 20261019-000000 · pelly 0.9.0 · 모델 `qwen3`", report);
        Assert.DoesNotContain("Ollama", report.Split('\n')[2]);
    }

    [Fact]
    public void Export_RepeatSummary_IsReadByCompare()
    {
        var input = MakeRepeat();
        var (code, _, dest) = Export(input);
        Assert.Equal(0, code);
        var summary = System.IO.Path.Combine(dest, "summary.json");
        Assert.Equal(RepeatSummary.ToJson(RepeatSummary.FromJson(File.ReadAllText(System.IO.Path.Combine(input, "summary.json")))),
            File.ReadAllText(summary));
        Assert.True(File.Exists(System.IO.Path.Combine(dest, "summary.md")));
        for (var k = 1; k <= 3; k++)
        {
            Assert.True(File.Exists(System.IO.Path.Combine(dest, $"run-{k}", "results.json")));
            Assert.True(File.Exists(System.IO.Path.Combine(dest, $"run-{k}", "report.md")));
        }
        Assert.Equal(3, CompareCommand.Run([summary, summary], new StringWriter()));
    }

    [Fact]
    public void Export_WritesExportReadme()
    {
        var (code, _, dest) = Export(MakeSingle("my-results"));
        Assert.Equal(0, code);
        var text = File.ReadAllText(System.IO.Path.Combine(dest, "EXPORT.md"));
        Assert.Contains("my-results", text);
        Assert.DoesNotContain(_dir, text);
        foreach (var field in new[] { "reasons", "independent.detail", "skipReason" }) Assert.Contains(field, text);
    }

    [Fact]
    public void Export_RefusesNonEmptyOut_And_MissingInput()
    {
        var input = MakeSingle();
        var full = System.IO.Path.Combine(_dir, "full");
        Directory.CreateDirectory(full);
        File.WriteAllText(System.IO.Path.Combine(full, "a.txt"), "x");
        Assert.Equal(4, Export(input, full).Code);
        Assert.Equal(4, Export(System.IO.Path.Combine(_dir, "nope")).Code);
        Assert.Equal(4, Export(Directory.CreateDirectory(System.IO.Path.Combine(_dir, "empty")).FullName).Code);
    }

    [Fact]
    public void Parse_Export_NeedsOneInputAndOut()
    {
        Assert.Null(EvalArguments.Parse(["export", "in", "--out", "o"]).Error);
        Assert.NotNull(EvalArguments.Parse(["export", "in"]).Error);
        Assert.NotNull(EvalArguments.Parse(["export", "--out", "o"]).Error);
        Assert.NotNull(EvalArguments.Parse(["export", "a", "b", "--out", "o"]).Error);
        Assert.NotNull(EvalArguments.Parse(["export", "in", "--out", "o", "--tasks", "x"]).Error);
    }
}
