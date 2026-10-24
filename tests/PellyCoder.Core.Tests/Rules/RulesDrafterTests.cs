using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Tests.Rules;

public class RulesDrafterTests
{
    private static MockFileSystem Fs() => new(new Dictionary<string, MockFileData>(), "/w");

    private static string Methods(int n, Func<int, string> name, string body = "await Task.Delay(1);") =>
        string.Concat(Enumerable.Range(0, n).Select(i => $"public async Task {name(i)}() {{ {body} }}\n"));

    private static string Wrap(string members, string usings = "") =>
        $"{usings}using System.Threading.Tasks;\nclass C {{\n{members}}}\n";

    private static DraftResult Run(MockFileSystem fs, string? cs = null, string? sql = null)
    {
        if (cs is not null) fs.AddFile("/w/src/A.cs", new MockFileData(cs));
        if (sql is not null) fs.AddFile("/w/db/a.sql", new MockFileData(sql));
        return RulesDrafter.Draft(fs, "/w", TestContext.Current.CancellationToken);
    }

    private static DraftRule? Find(DraftResult r, string fragment) => r.Rules.FirstOrDefault(x => x.Line.Contains(fragment));

    [Fact]
    public void AsyncSuffix_TenOfTen_Emits()
    {
        var r = Run(Fs(), Wrap(Methods(10, i => $"M{i}Async")));
        var rule = Assert.Single(r.Rules, x => x.Line.StartsWith("- [async] async 메서드"));
        Assert.Equal("async 메서드 10개 중 10개가 Async로 끝남", rule.Evidence);
    }

    [Fact]
    public void AsyncSuffix_NineSamples_NoRule() =>
        Assert.Null(Find(Run(Fs(), Wrap(Methods(9, i => $"M{i}Async"))), "Async로 끝낸다"));

    [Fact]
    public void AsyncSuffix_BelowRatio_NoRule() =>
        Assert.Null(Find(Run(Fs(), Wrap(Methods(8, i => $"M{i}Async") + Methods(2, i => $"N{i}"))), "Async로 끝낸다"));

    [Fact]
    public void AsyncSuffix_ExcludesOverrideAndMain()
    {
        var members = Methods(10, i => $"M{i}Async") + "public override async Task Foo() { await Task.Delay(1); }\nstatic async Task Main() { await Task.Delay(1); }\n";
        Assert.NotNull(Find(Run(Fs(), Wrap(members)), "Async로 끝낸다"));
    }

    [Fact]
    public void NoBlocking_NoUse_Emits()
    {
        var r = Run(Fs(), Wrap(Methods(10, i => $"M{i}Async")));
        var rule = Assert.IsType<DraftRule>(Find(r, "forbid:"));
        Assert.Equal(@"- [async] .Result나 .Wait()로 비동기 결과를 기다리지 않는다 | forbid: \.Result\b|\.Wait\(\)", rule.Line);
        Assert.Equal("async 메서드 10개, .Result/.Wait()/GetResult() 사용 0건", rule.Evidence);
    }

    [Theory]
    [InlineData("var x = Task.FromResult(1).Result;")]
    [InlineData("Task.Delay(1).Wait();")]
    [InlineData("Task.Delay(1).GetAwaiter().GetResult();")]
    public void NoBlocking_OneUse_NoRule(string statement) =>
        Assert.Null(Find(Run(Fs(), Wrap(Methods(10, i => $"M{i}Async") + $"void Sync() {{ {statement} }}\n")), "forbid:"));

    [Fact]
    public void NoBlocking_WaitWithArgument_NotBlocking() =>
        Assert.NotNull(Find(Run(Fs(), Wrap(Methods(10, i => $"M{i}Async") + "void Sync() { Task.Delay(1).Wait(5); }\n")), "forbid:"));

    [Fact]
    public void NoBlocking_NineAsyncMethods_NoRule() =>
        Assert.Null(Find(Run(Fs(), Wrap(Methods(9, i => $"M{i}Async"))), "forbid:"));

    [Fact]
    public void ConfigureAwait_TwentyOfTwenty_Emits()
    {
        var r = Run(Fs(), Wrap(Methods(20, i => $"M{i}Async", "await Task.Delay(1).ConfigureAwait(false);")));
        Assert.Equal("await 20개 중 20개에 ConfigureAwait(false)", Assert.IsType<DraftRule>(Find(r, "ConfigureAwait(false)를")).Evidence);
    }

    [Fact]
    public void ConfigureAwait_BelowRatioOrSample_NoRule()
    {
        Assert.Null(Find(Run(Fs(), Wrap(Methods(19, i => $"M{i}Async", "await Task.Delay(1).ConfigureAwait(false);"))), "ConfigureAwait"));
        var mixed = Methods(15, i => $"M{i}Async", "await Task.Delay(1).ConfigureAwait(false);") + Methods(5, i => $"N{i}Async");
        Assert.Null(Find(Run(Fs(), Wrap(mixed)), "ConfigureAwait"));
    }

    [Fact]
    public void Dapper_UsingPresent_Emits()
    {
        var r = Run(Fs(), Wrap("", "using Dapper;\n"));
        Assert.Equal("using Dapper; 파일 1개", Assert.IsType<DraftRule>(Find(r, "[dapper]")).Evidence);
    }

    [Fact]
    public void Dapper_Absent_NoRule() => Assert.Null(Find(Run(Fs(), Wrap("")), "[dapper]"));

    private static string Comments(int korean, int other) =>
        string.Concat(Enumerable.Range(0, korean).Select(_ => "// 한국어 주석\n")) + string.Concat(Enumerable.Range(0, other).Select(_ => "// english\n"));

    [Fact]
    public void KoreanComments_TwentyOfTwenty_Emits()
    {
        var r = Run(Fs(), "class C {\n" + Comments(19, 0) + "/* 블록 */ /// <summary>요약</summary>\nint x;\n}\n");
        var rule = Assert.IsType<DraftRule>(Find(r, "주석은 한국어로"));
        Assert.StartsWith("- [*] ", rule.Line);
        Assert.Equal("주석 21개 중 21개가 한국어", rule.Evidence);
    }

    [Fact]
    public void KoreanComments_BelowRatioOrSample_NoRule()
    {
        Assert.Null(Find(Run(Fs(), "class C {\n" + Comments(19, 0) + "}\n"), "주석은"));
        Assert.Null(Find(Run(Fs(), "class C {\n" + Comments(11, 9) + "}\n"), "주석은"));
    }

    private static string Throws(int korean, int english) =>
        "class C {\nvoid F() {\n" +
        string.Concat(Enumerable.Range(0, korean).Select(_ => "if (a) throw new System.Exception(\"실패했습니다\");\n")) +
        string.Concat(Enumerable.Range(0, english).Select(_ => "if (a) throw new System.Exception($\"failed {a}\");\n")) +
        "}\n}\n";

    [Fact]
    public void ExceptionLanguage_Korean_EmitsKoreanRule()
    {
        var r = Run(Fs(), Throws(10, 0));
        var rule = Assert.IsType<DraftRule>(Find(r, "[throw]"));
        Assert.Equal("- [throw] 예외 메시지는 한국어로 작성한다", rule.Line);
        Assert.Equal("throw 메시지 10개 중 10개가 한국어", rule.Evidence);
    }

    [Fact]
    public void ExceptionLanguage_English_EmitsEnglishRule()
    {
        var r = Run(Fs(), Throws(1, 9));
        var rule = Assert.IsType<DraftRule>(Find(r, "[throw]"));
        Assert.Equal("- [throw] 예외 메시지는 영어로 작성한다", rule.Line);
        Assert.Equal("throw 메시지 10개 중 9개가 한국어 아님", rule.Evidence);
    }

    [Fact]
    public void ExceptionLanguage_MixedOrFewSamples_NoRule()
    {
        Assert.Null(Find(Run(Fs(), Throws(5, 5)), "[throw]"));
        Assert.Null(Find(Run(Fs(), Throws(0, 9)), "[throw]"));
    }

    [Fact]
    public void ExceptionLanguage_NonLiteralFirstArgument_NotCounted() =>
        Assert.Null(Find(Run(Fs(), "class C { void F(string m) {\n" + string.Concat(Enumerable.Repeat("throw new System.Exception(m);\n", 12)) + "} }\n"), "[throw]"));

    private static string Locals(int var, int explicitType) =>
        "class C { void F() {\n" +
        string.Concat(Enumerable.Range(0, var).Select(i => $"var a{i} = 1;\n")) +
        string.Concat(Enumerable.Range(0, explicitType).Select(i => $"int b{i} = 1;\n")) +
        "foreach (var q in new int[0]) { }\n} }\n";

    [Fact]
    public void VarStyle_Mostly_EmitsVarRule()
    {
        var r = Run(Fs(), Locals(40, 10));
        var rule = Assert.IsType<DraftRule>(Find(r, "var로 선언한다"));
        Assert.StartsWith("- [*] ", rule.Line);
        Assert.Equal("지역 변수 선언 50개 중 40개가 var", rule.Evidence);
    }

    [Fact]
    public void VarStyle_Rarely_EmitsExplicitRule()
    {
        var r = Run(Fs(), Locals(10, 40));
        Assert.Equal("지역 변수 선언 50개 중 40개가 명시적 타입", Assert.IsType<DraftRule>(Find(r, "명시적 타입으로")).Evidence);
    }

    [Fact]
    public void VarStyle_MiddleOrFewSamples_NoRule()
    {
        Assert.Null(Find(Run(Fs(), Locals(25, 25)), "지역 변수"));
        Assert.Null(Find(Run(Fs(), Locals(49, 0)), "지역 변수"));
    }

    private static string Proc(string name, bool nocount, string kw = "PROCEDURE") =>
        $"CREATE {kw} dbo.{name} AS\nBEGIN\n{(nocount ? "  set nocount on;\n" : "")}  SELECT 1;\nEND\nGO\n";

    [Fact]
    public void SqlNocount_ThreeOfThree_Emits()
    {
        var r = Run(Fs(), sql: Proc("A", true) + Proc("B", true, "PROC") + Proc("C", true));
        Assert.Equal("프로시저 3개 중 3개에 SET NOCOUNT ON", Assert.IsType<DraftRule>(Find(r, "[procedure]")).Evidence);
        Assert.Equal(1, r.SqlFiles);
    }

    [Fact]
    public void SqlNocount_TwoSamplesOrLowRatio_NoRule()
    {
        Assert.Null(Find(Run(Fs(), sql: Proc("A", true) + Proc("B", true)), "[procedure]"));
        Assert.Null(Find(Run(Fs(), sql: Proc("A", true) + Proc("B", false) + Proc("C", false)), "[procedure]"));
    }

    [Fact]
    public void SqlNocount_BodyEndsAtNextDefinitionAndGo()
    {
        // NOCOUNT in a later procedure (or after GO) must not be credited to the earlier one.
        var sql = "CREATE PROC dbo.A AS SELECT 1\nCREATE PROC dbo.B AS SET NOCOUNT ON\nGO\nSET NOCOUNT ON\nCREATE PROC dbo.C AS SELECT 1\n";
        Assert.Null(Find(Run(Fs(), sql: sql), "[procedure]"));
    }

    [Fact]
    public void SqlNocount_CommentedOut_NotCounted() =>
        Assert.Null(Find(Run(Fs(), sql: Proc("A", false).Replace("SELECT 1", "-- SET NOCOUNT ON\nSELECT 1") + Proc("B", false) + Proc("C", false)), "[procedure]"));

    private static string Selects(int qualified, int bare) =>
        "SELECT * FROM dbo.T0\n" + string.Concat(Enumerable.Range(1, qualified - 1).Select(i => $"JOIN [dbo].[T{i}] ON 1=1\n")) +
        string.Concat(Enumerable.Range(0, bare).Select(i => $"SELECT * FROM U{i}\n")) +
        "SELECT * FROM #tmp\nSELECT * FROM @tv\nSELECT * FROM (SELECT 1) x -- FROM nothing\n";

    [Fact]
    public void SqlSchema_Qualified_Emits()
    {
        var r = Run(Fs(), sql: Selects(18, 2));
        Assert.Equal("FROM/JOIN 객체 20개 중 18개에 스키마 이름", Assert.IsType<DraftRule>(Find(r, "스키마 이름")).Evidence);
    }

    [Fact]
    public void SqlSchema_LowRatioOrFewSamples_NoRule()
    {
        Assert.Null(Find(Run(Fs(), sql: Selects(17, 3)), "스키마 이름"));
        Assert.Null(Find(Run(Fs(), sql: Selects(19, 0)), "스키마 이름"));
    }

    [Fact]
    public void Draft_SkipsBinObj()
    {
        var fs = Fs();
        fs.AddFile("/w/bin/A.cs", new MockFileData(Wrap(Methods(10, i => $"M{i}Async"))));
        fs.AddFile("/w/obj/B.cs", new MockFileData("using Dapper;"));
        fs.AddFile("/w/obj/c.sql", new MockFileData(Proc("A", true)));
        var r = RulesDrafter.Draft(fs, "/w", TestContext.Current.CancellationToken);
        Assert.Empty(r.Rules);
        Assert.Equal(0, r.CsFiles);
        Assert.Equal(0, r.SqlFiles);
    }

    [Fact]
    public void Draft_SkipsOversizeAndUndecodable()
    {
        var fs = Fs();
        fs.AddFile("/w/Big.cs", new MockFileData("using Dapper;\n" + new string(' ', 512 * 1024)));
        fs.AddFile("/w/Bad.cs", new MockFileData(new byte[] { 0xEF, 0xBB, 0xBF, 0xFF, 0xFE, 0xFD }));
        var r = RulesDrafter.Draft(fs, "/w", TestContext.Current.CancellationToken);
        Assert.Empty(r.Rules);
        Assert.Equal(0, r.CsFiles);
    }

    [Fact]
    public void Draft_AllLinesParse_AndStarRulesComeFirst()
    {
        var cs = "using Dapper;\nusing System.Threading.Tasks;\nclass C {\n"
            + Methods(20, i => $"M{i}Async", "await Task.Delay(1).ConfigureAwait(false);")
            + "void F() {\n"
            + string.Concat(Enumerable.Range(0, 60).Select(i => $"// 주석\nvar v{i} = 1;\n"))
            + string.Concat(Enumerable.Repeat("if (a) throw new System.Exception(\"실패\");\n", 10))
            + "}\n}\n";
        var sql = Proc("A", true) + Proc("B", true) + Proc("C", true) + Selects(20, 0);
        var fs = Fs();
        var r = Run(fs, cs, sql);

        Assert.Equal(9, r.Rules.Count);
        var firstNonStar = r.Rules.ToList().FindIndex(x => !x.Line.StartsWith("- [*]"));
        Assert.All(r.Rules.Skip(firstNonStar), x => Assert.DoesNotContain("- [*]", x.Line));
        Assert.Equal(2, firstNonStar);

        var text = "## Pelly rules\n" + string.Join("\n", r.Rules.Select(x => x.Line)) + "\n";
        var (rules, errors) = RulesDocument.Parse(text);
        Assert.Empty(errors);
        Assert.Equal(r.Rules.Count, rules.Count);
    }

    [Fact]
    public void Draft_Corpus_IsDeterministic()
    {
        var a = RulesDrafter.Draft(new FileSystem(), CorpusLocation.Get(""), TestContext.Current.CancellationToken);
        var b = RulesDrafter.Draft(new FileSystem(), CorpusLocation.Get(""), TestContext.Current.CancellationToken);
        Assert.Equal(a.CsFiles, b.CsFiles);
        Assert.Equal(a.SqlFiles, b.SqlFiles);
        Assert.Equal(a.Rules, b.Rules);
        Assert.True(a.CsFiles > 0);
    }
}
