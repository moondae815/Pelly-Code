using System.IO.Abstractions;
using PellyCoder.Eval;
using PellyCoder.Integration.Tests.Corpus;

namespace PellyCoder.Integration.Tests.Benchmark;

public class BenchmarkDefinitionTests
{
    private static readonly string EvalRoot = Directory.GetParent(CorpusPaths.Root)!.FullName;
    private static readonly BenchmarkSuite Suite = BenchmarkSuite.Load(Path.Combine(EvalRoot, "benchmark")).Suite
        ?? throw new InvalidDataException("내장 스위트(eval/benchmark/suite.json)를 읽을 수 없습니다");
    private static readonly string TasksDir = Suite.TasksDir;

    /// <summary>The task's original file, resolved the way the runner does (<see cref="BenchmarkSuite.Resolve"/>).</summary>
    private static SuiteTask Resolve(BenchmarkTask t) =>
        Suite.Resolve(t, new FileSystem(), out var error) ?? throw new InvalidDataException(error);

    private static IReadOnlyList<(string Path, BenchmarkTask Task)> LoadAll()
    {
        var files = Directory.GetFiles(TasksDir, "*.yaml");
        Assert.NotEmpty(files);
        return files.Select(f => (f, BenchmarkTask.Load(f))).ToList();
    }

    /// <summary>
    /// M3b 구성: C# 30(Legacy 18, Modern 12) + SQL 20. Legacy 범주(YAML <c>category</c>, 앱 종류)는 MVC 8 / ADO.NET 5 / Dapper 5이고,
    /// 범주는 Legacy에만 둔다(합격 판정의 범주 cs-legacy·cs-modern·sql은 id 접두사다). Modern은 모두 동작 판정을 가진다(R1).
    /// </summary>
    [Fact]
    public void Composition_Is30Cs20Sql_With18Legacy()
    {
        var tasks = LoadAll().Select(x => x.Task).ToList();
        Assert.Equal(30, tasks.Count(t => t.Kind == "cs"));
        Assert.Equal(20, tasks.Count(t => t.Kind == "sql"));
        var legacy = tasks.Where(t => t.Id.StartsWith("cs-legacy-", StringComparison.Ordinal)).ToList();
        var modern = tasks.Where(t => t.Id.StartsWith("cs-modern-", StringComparison.Ordinal)).ToList();
        Assert.Equal(18, legacy.Count);
        Assert.Equal(12, modern.Count);
        Assert.All(legacy, t => Assert.True(Resolve(t).IsLegacy, $"{t.Id}: Legacy(비 SDK) 프로젝트 대상이어야 함"));
        Assert.All(legacy, t => Assert.StartsWith("Legacy/", t.File, StringComparison.Ordinal));
        Assert.All(modern, t => Assert.StartsWith("Modern/", t.File, StringComparison.Ordinal));

        Assert.Equal(8, legacy.Count(t => t.Category == "mvc"));
        Assert.Equal(5, legacy.Count(t => t.Category == "adonet"));
        Assert.Equal(5, legacy.Count(t => t.Category == "dapper"));
        Assert.All(tasks.Where(t => !Resolve(t).IsLegacy), t => Assert.True(t.Category is null, $"{t.Id}: category는 Legacy 작업에만"));

        Assert.True(tasks.Count(t => t.Line is not null) >= 3, "line 지정 작업 3건 이상");
        Assert.True(tasks.Count(t => t.Object is not null) >= 3, "object 지정 작업 3건 이상");

        var unjudged = modern.Where(t => (t.Judge!.Tests?.Count ?? 0) + (t.Judge.FailingTests?.Count ?? 0) == 0).Select(t => t.Id).ToList();
        Assert.Empty(unjudged);
    }

    /// <summary>
    /// 내장 스위트의 정의 검사(<c>validate</c>와 같은 <see cref="SuiteValidator"/>): id·kind·judge 규칙, 대상 존재, 원본 판정 불통과 등.
    /// 오류는 0이고, 경고는 하나만 허용한다: Legacy 루트의 packages.config는 온라인 NuGet 복원을 쓰는 내장 코퍼스의 의도된 모양이라
    /// "packages/ 폴더 없음" 경고가 뜬다(현장 스위트에서는 폐쇄망 문제라 경고가 맞다). 이슈 전체를 실패 메시지로 낸다.
    /// </summary>
    [Fact]
    public void BuiltInSuite_HasNoIssues()
    {
        var (tasks, errors) = BenchmarkTask.TryLoadAll(TasksDir);
        Assert.NotEmpty(tasks);
        var issues = SuiteValidator.Validate(Suite, tasks, errors, new FileSystem(), compatLevel: 160);
        issues = [.. issues.Where(i => !(i.Level == IssueLevel.Warning && i.Message.StartsWith("루트 Legacy: packages.config가 있지만", StringComparison.Ordinal)))];
        Assert.True(issues.Count == 0, string.Join("\n", issues.Select(i => $"{i.Level} {i.TaskId}: {i.Message}")));
    }
}
