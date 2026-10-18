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
    private static readonly string TasksDir = Path.Combine(EvalRoot, "benchmark", "tasks");

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
            var file = Path.Combine(EvalRoot, t.File.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"{t.Id}: 파일 없음 {t.File}");
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

    [Fact]
    public void Composition_Is18Cs12Sql_With10Legacy()
    {
        var tasks = LoadAll().Select(x => x.Task).ToList();
        Assert.Equal(18, tasks.Count(t => t.Kind == "cs"));
        Assert.Equal(12, tasks.Count(t => t.Kind == "sql"));
        Assert.Equal(10, tasks.Count(t => t.Id.StartsWith("cs-legacy-", StringComparison.Ordinal)));
        Assert.True(tasks.Count(t => t.Line is not null) >= 2, "line 지정 작업 2건 이상");
        Assert.True(tasks.Count(t => t.Object is not null) >= 2, "object 지정 작업 2건 이상");
    }

    /// <summary>
    /// 빌드·컴파일만 통과하면 되는 작업은 아무 의미 없는 수정(공백·주석)으로도 성공한다:
    /// 모든 작업에 내용 판정(mustContain·mustAdd·mustNotContain) 하나 이상을 요구한다.
    /// </summary>
    [Fact]
    public void AllTasks_HaveContentCriterion()
    {
        var missing = LoadAll().Select(x => x.Task)
            .Where(t => (t.Judge!.MustContain?.Count ?? 0) + (t.Judge.MustAdd?.Count ?? 0) + (t.Judge.MustNotContain?.Count ?? 0) == 0)
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
            var text = ReadSource(Path.Combine(EvalRoot, t.File.Replace('/', Path.DirectorySeparatorChar)))
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
            var text = ReadSource(Path.Combine(EvalRoot, t.File.Replace('/', Path.DirectorySeparatorChar)))
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
