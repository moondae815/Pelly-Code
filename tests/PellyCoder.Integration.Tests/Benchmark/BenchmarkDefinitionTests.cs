using System.Text;
using System.Text.RegularExpressions;
using PellyCoder.Integration.Tests.Corpus;

namespace PellyCoder.Integration.Tests.Benchmark;

public class BenchmarkDefinitionTests
{
    static BenchmarkDefinitionTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly string EvalRoot = Directory.GetParent(CorpusPaths.Root)!.FullName;
    private static readonly string TasksDir = Path.Combine(EvalRoot, "benchmark", "tasks");

    private static IReadOnlyList<(string Path, BenchmarkTaskDefinition Task)> LoadAll()
    {
        var files = Directory.GetFiles(TasksDir, "*.yaml");
        Assert.NotEmpty(files);
        return files.Select(f => (f, BenchmarkTaskDefinition.Load(f))).ToList();
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
    public void Composition_Is5Cs5Sql_With3Legacy()
    {
        var tasks = LoadAll().Select(x => x.Task).ToList();
        Assert.Equal(5, tasks.Count(t => t.Kind == "cs"));
        Assert.Equal(5, tasks.Count(t => t.Kind == "sql"));
        Assert.Equal(3, tasks.Count(t => t.Id.StartsWith("cs-legacy-")));
        Assert.Contains(tasks, t => t.Line is not null);
    }

    /// <summary>
    /// 아무것도 고치지 않은 패치가 판정을 통과하면 안 된다:
    /// mustContain은 원본 대상에 없어야 하고, mustNotContain은 원본 대상에 있어야 한다.
    /// 대상 범위: method 지정이면 그 멤버, 그 외(line·sql)는 파일 전체.
    /// </summary>
    [Fact]
    public void Judges_RejectUnmodifiedTarget()
    {
        var problems = new List<string>();
        foreach (var (_, t) in LoadAll())
        {
            var text = ReadSource(Path.Combine(EvalRoot, t.File.Replace('/', Path.DirectorySeparatorChar)));
            var scope = t.Method is not null ? MemberText(text, t.Method) : text;
            foreach (var s in t.Judge!.MustContain ?? [])
                if (scope.Contains(s, StringComparison.Ordinal))
                    problems.Add($"{t.Id}: mustContain '{s}'가 원본에 이미 있음");
            foreach (var s in t.Judge.MustNotContain ?? [])
                if (!scope.Contains(s, StringComparison.Ordinal))
                    problems.Add($"{t.Id}: mustNotContain '{s}'가 원본에 없음");
        }

        Assert.Empty(problems);
    }

    /// <summary>메서드 선언 줄부터 같은 들여쓰기의 닫는 중괄호까지.</summary>
    private static string MemberText(string text, string method)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var decl = new Regex($@"^\s*(public|private|protected|internal)\b.*\b{Regex.Escape(method)}\s*\(");
        var start = Array.FindIndex(lines, l => decl.IsMatch(l));
        Assert.True(start >= 0, $"{method} 선언 없음");
        var closing = new string(' ', lines[start].Length - lines[start].TrimStart().Length) + "}";
        var end = Array.FindIndex(lines, start + 1, l => l == closing);
        Assert.True(end > start, $"{method} 끝 없음");
        return string.Join("\n", lines[start..(end + 1)]);
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
