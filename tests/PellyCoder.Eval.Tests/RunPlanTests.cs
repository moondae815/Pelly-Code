using System.IO.Abstractions;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

/// <summary>
/// What <c>run</c> does with a suite before any LLM call (I4, I5, m1): unreadable task files stop the run (exit 4),
/// schema-invalid tasks and targets that cannot be resolved become Definition failures without running, a non-empty
/// <c>--out</c> is refused.
/// </summary>
public sealed class RunPlanTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "pelly-runplan-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _suiteDir;

    public RunPlanTests()
    {
        _suiteDir = Path.Combine(_tmp, "suite");
        Directory.CreateDirectory(Path.Combine(_suiteDir, "tasks"));
        Directory.CreateDirectory(Path.Combine(_tmp, "App", "Sdk"));
        File.WriteAllText(Path.Combine(_tmp, "App", "Sdk", "Sdk.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(Path.Combine(_tmp, "App", "Sdk", "Calc.cs"), "namespace N;\npublic class Calc\n{\n    public int Add(int a, int b) => a + b;\n}\n");
        File.WriteAllText(Path.Combine(_suiteDir, "suite.json"),
            "{ \"formatVersion\": 1, \"name\": \"t\", \"roots\": [ { \"name\": \"App\", \"path\": \"../App\" } ] }");
        Task("cs-modern-001", "cs-modern-001");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    private void Task(string fileName, string id) =>
        File.WriteAllText(Path.Combine(_suiteDir, "tasks", fileName + ".yaml"),
            $"id: {id}\nkind: cs\nfile: App/Sdk/Calc.cs\nmethod: Add\nrequest: \"r\"\njudge:\n  build: true\n  mustContain: [\"a - b\"]\n");

    private BenchmarkSuite Suite() => BenchmarkSuite.Load(_suiteDir).Suite!;

    [Fact]
    public void Prepare_ValidTask_IsResolved()
    {
        var plan = RunPlan.Prepare(Suite(), new FileSystem(), null, null);
        Assert.Empty(plan.LoadErrors);
        var task = Assert.Single(plan.Tasks);
        Assert.NotNull(task.Resolved);
        Assert.Null(task.Error);
    }

    [Fact]
    public void Prepare_UnreadableYaml_ReturnsLoadErrors_InsteadOfThrowing()
    {
        File.WriteAllText(Path.Combine(_suiteDir, "tasks", "cs-modern-002.yaml"), "id: [unclosed");
        var plan = RunPlan.Prepare(Suite(), new FileSystem(), null, null);
        Assert.Contains(plan.LoadErrors, e => e.StartsWith("cs-modern-002.yaml: YAML을 읽을 수 없음", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cs-modern-ERP", "cs-modern-ERP")]       // business name in the id
    [InlineData("cs-modern-002", "cs-modern-003")]       // id differs from the file name
    [InlineData("x", "")]                                // empty id
    public void Prepare_SchemaInvalidTask_IsDefinitionFailure_NotResolved(string fileName, string id)
    {
        Task(fileName, id);
        var plan = RunPlan.Prepare(Suite(), new FileSystem(), null, null);
        var bad = plan.Tasks.Single(t => t.Task.SourceName == fileName);
        Assert.Null(bad.Resolved);
        Assert.Contains("작업 정의 오류", bad.Error, StringComparison.Ordinal);
        Assert.NotNull(plan.Tasks.Single(t => t.Task.Id == "cs-modern-001").Resolved);
    }

    [Fact]
    public void Prepare_CsprojThatCannotBeRead_IsDefinitionFailure_NotACrash()
    {
        var plan = RunPlan.Prepare(Suite(), new SuiteValidatorTests.CsprojReadFails("denied"), null, null);
        var task = Assert.Single(plan.Tasks);
        Assert.Null(task.Resolved);
        Assert.Contains("대상 프로젝트 파일을 읽을 수 없음", task.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_AppliesTheTaskFilter()
    {
        Task("cs-modern-002", "cs-modern-002");
        var plan = RunPlan.Prepare(Suite(), new FileSystem(), "cs-modern-002", null);
        Assert.Equal("cs-modern-002", Assert.Single(plan.Tasks).Task.Id);
    }

    [Fact]
    public void OutConflict_RefusesNonEmptyFolder_AllowsMissingOrEmpty()
    {
        var outDir = Path.Combine(_tmp, "out");
        Assert.Null(RunPlan.OutConflict(outDir));
        Directory.CreateDirectory(outDir);
        Assert.Null(RunPlan.OutConflict(outDir));
        File.WriteAllText(Path.Combine(outDir, "results.json"), "[]");
        Assert.StartsWith($"--out 폴더가 비어 있지 않습니다: {outDir}", RunPlan.OutConflict(outDir), StringComparison.Ordinal);
    }
}
