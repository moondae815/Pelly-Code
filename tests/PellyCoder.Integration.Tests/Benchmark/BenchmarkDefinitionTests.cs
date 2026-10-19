using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using PellyCoder.Core.Sql;
using PellyCoder.Eval;
using PellyCoder.Integration.Tests.Corpus;

namespace PellyCoder.Integration.Tests.Benchmark;

public class BenchmarkDefinitionTests
{
    static BenchmarkDefinitionTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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

    [Fact]
    public void AllTasks_ParseAndMatchSchema()
    {
        foreach (var (path, t) in LoadAll())
        {
            var name = Path.GetFileNameWithoutExtension(path);
            Assert.True(Regex.IsMatch(t.Id, @"^(cs-(modern|legacy)|sql)-\d{3}$"), $"{name}: id 형식");
            Assert.Equal(name, t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Request), $"{t.Id}: request 필요");
            Assert.NotNull(t.Judge);

            switch (t.Kind)
            {
                case "cs":
                    Assert.True(t.Method is not null ^ t.Line is not null, $"{t.Id}: cs는 method 또는 line 중 하나");
                    Assert.Null(t.Object);
                    Assert.True(t.Judge!.Build, $"{t.Id}: judge.build 필요");
                    Assert.Null(t.Judge.SqlCompile);
                    break;
                case "sql":
                    Assert.Null(t.Method);
                    Assert.StartsWith("sql-", t.Id);
                    Assert.True(t.Judge!.SqlCompile, $"{t.Id}: judge.sqlCompile 필요");
                    Assert.Null(t.Judge.Build);
                    break;
                default:
                    Assert.Fail($"{t.Id}: 알 수 없는 kind '{t.Kind}'");
                    break;
            }
        }
    }

    [Fact]
    public void AllTasks_TargetsExist()
    {
        foreach (var (_, t) in LoadAll())
        {
            var resolved = Suite.Resolve(t, new FileSystem(), out var error);
            Assert.True(resolved is not null, error);
            var file = resolved.SourceFile;
            var text = ReadSource(file);

            if (t.Method is not null)
                Assert.Matches(new Regex($@"\b{Regex.Escape(t.Method)}\s*\("), text);
            if (t.Line is not null)
                Assert.InRange(t.Line.Value, 1, text.Split('\n').Length);
            if (t.Object is not null)
                Assert.Contains(t.Object, text);
        }
    }

    [Fact]
    public void TaskIds_AreUnique()
    {
        var ids = LoadAll().Select(x => x.Task.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
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
    /// 빌드·컴파일만 통과하면 되는 작업은 아무 의미 없는 수정(공백·주석)으로도 성공한다:
    /// 모든 작업에 내용 판정(mustContain·mustAdd·mustNotContain) 또는 동작 판정(tests·failingTests) 하나 이상을 요구한다.
    /// tests는 원본에서 실패하고 failingTests는 원본에서 통과해야 하므로(JudgeTestDefinitionTests) 의미 없는 수정을 똑같이 막는다.
    /// </summary>
    [Fact]
    public void AllTasks_HaveContentCriterion()
    {
        var missing = LoadAll().Select(x => x.Task)
            .Where(t => (t.Judge!.MustContain?.Count ?? 0) + (t.Judge.MustAdd?.Count ?? 0) + (t.Judge.MustNotContain?.Count ?? 0)
                + (t.Judge.Tests?.Count ?? 0) + (t.Judge.FailingTests?.Count ?? 0) == 0)
            .Select(t => t.Id)
            .ToList();
        Assert.Empty(missing);
    }

    /// <summary>sql 작업의 대상은 GO로 나눈 객체 배치 정확히 하나여야 한다(object가 없으면 파일의 유일한 객체).</summary>
    [Fact]
    public void SqlTasks_TargetIsAnObjectBatch()
    {
        var problems = new List<string>();
        foreach (var (_, t) in LoadAll().Where(x => x.Task.Kind == "sql"))
        {
            var text = ReadSource(Resolve(t).SourceFile)
                .TrimStart('\uFEFF').Replace("\r\n", "\n");
            var script = SqlBatchSplitter.Split(text, compatLevel: 160);
            var batches = t.Object is null
                ? script.ObjectBatches
                : script.ObjectBatches.Where(b => b.ObjectName is { } n && n.SameAs(SqlObjectName.Parse(t.Object))).ToList();
            if (batches.Count != 1)
                problems.Add($"{t.Id}: 대상 객체 배치 {batches.Count}개");
            else if (batches[0].ParseErrors.Count > 0)
                problems.Add($"{t.Id}: 원본 대상 배치에 구문 오류 {batches[0].ParseErrors.Count}개");
        }

        Assert.Empty(problems);
    }

    /// <summary>
    /// 아무것도 고치지 않은 패치가 판정을 통과하면 안 된다:
    /// mustContain은 원본 대상에 없어야 하고, mustNotContain은 원본 대상에 있어야 한다.
    /// 대상 범위는 실행기와 같다(<see cref="TargetText"/>): method·line이면 그 멤버, sql이면 대상 객체 배치.
    /// </summary>
    [Fact]
    public void Judges_RejectUnmodifiedTarget()
    {
        var problems = new List<string>();
        foreach (var (_, t) in LoadAll())
        {
            var text = ReadSource(Resolve(t).SourceFile)
                .TrimStart('\uFEFF').Replace("\r\n", "\n");
            var scope = t.Kind == "sql"
                ? TargetText.ForSql(text, t.Object, compatLevel: 160)
                : TargetText.ForCs(text, t.Method, t.Line);
            if (scope is null)
            {
                problems.Add($"{t.Id}: 원본에서 대상을 찾지 못함");
                continue;
            }

            foreach (var s in t.Judge!.MustContain ?? [])
                if (scope.Contains(s, StringComparison.Ordinal))
                    problems.Add($"{t.Id}: mustContain '{s}'가 원본에 이미 있음");
            foreach (var s in t.Judge.MustNotContain ?? [])
                if (!scope.Contains(s, StringComparison.Ordinal))
                    problems.Add($"{t.Id}: mustNotContain '{s}'가 원본에 없음");
        }

        Assert.Empty(problems);
    }

    /// <summary>UTF-8로 읽고, 올바른 UTF-8이 아니면 CP949로 읽는다.</summary>
    private static string ReadSource(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }
}
