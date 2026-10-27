using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Specs.Drafting;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs.Drafting;

public sealed class SpecDrafterTests
{
    private const string A =
        "public class A\n{\n    public int M(int x) => x;\n    public void Dup() { }\n    public void Dup(int y) { }\n}\n";

    private const string B =
        "public class B\n{\n    public B() { new A().M(0); }\n    public void Use() { new A().M(1); }\n}\n";

    private const string C =
        "public class C\n{\n    public void Run() { }\n}\n";

    private sealed class FakeImpact(ImpactResult result) : IImpactFinder
    {
        public List<IReadOnlyList<ImpactSeed>> Calls { get; } = [];
        public Exception? Throw { get; init; }

        public Task<ImpactResult> FindAsync(Workspace ws, IReadOnlyList<ImpactSeed> seeds, CancellationToken ct)
        {
            Calls.Add(seeds);
            if (Throw is not null) throw Throw;
            return Task.FromResult(result);
        }
    }

    private static MockFileSystem Fs()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/ws/App.sln", "");
        fs.AddFile("/ws/src/A.cs", A);
        fs.AddFile("/ws/src/B.cs", B);
        fs.AddFile("/ws/src/C.cs", C);
        return fs;
    }

    private static Workspace Ws(MockFileSystem fs)
    {
        var root = fs.Path.GetFullPath("/ws");
        return new Workspace(root, fs.Path.Combine(root, ".pelly"));
    }

    private static ImpactResult Semantic(params ImpactTarget[] targets) => new(ImpactMode.Semantic, null, targets, []);

    private sealed record Run(DraftOutcome? Outcome, DraftFailure? Failure, FakeDraftBackend Backend, FakeImpact Impact, MockFileSystem Fs);

    private static async Task<Run> Draft(string answer, ImpactResult? impact = null, MockFileSystem? fs = null, string request = "주문 취소를 막는다",
        string[]? scopes = null, FakeImpact? finder = null)
    {
        fs ??= Fs();
        var backend = new FakeDraftBackend(("thought", "stop"), (answer, "stop"));
        finder ??= new FakeImpact(impact ?? Semantic());
        var model = new DraftModel(backend, QwenChatTemplate.LoadDefault(), TimeProvider.System);
        var drafter = new SpecDrafter(fs, model, finder, null, 160);
        var (outcome, failure) = await drafter.DraftAsync(Ws(fs), new DraftRequest("d1", request, scopes ?? ["/ws/src"]), CancellationToken.None);
        return new Run(outcome, failure, backend, finder, fs);
    }

    private static string[] TaskLines(DraftOutcome o) =>
        o.Document.Split('\n').Where(l => l.StartsWith("- [", StringComparison.Ordinal)).ToArray();

    private static string[] Codes(DraftOutcome o) => o.Check.Issues.Select(i => i.Code).ToArray();

    [Fact]
    public async Task ScopeOverBudget_NoModelCall_Exit4()
    {
        var fs = Fs();
        var big = new StringBuilder("public class Big\n{\n");
        for (var i = 0; i < 600; i++) big.Append($"    public void MethodNumber{i}(int alpha, string beta) {{ }}\n");
        fs.AddFile("/ws/src/Big.cs", big.Append("}\n").ToString());

        var run = await Draft("- [ ] T1 cs src/A.cs#M x", fs: fs);

        Assert.Null(run.Outcome);
        Assert.Equal(4, run.Failure!.ExitCode);
        Assert.Contains("src/Big.cs", run.Failure.Message);
        Assert.Contains("--scope를 좁히십시오", run.Failure.Message);
        // The biggest file is named first.
        Assert.True(run.Failure.Message.IndexOf("src/Big.cs", StringComparison.Ordinal) < run.Failure.Message.IndexOf("src/A.cs", StringComparison.Ordinal));
        Assert.Empty(run.Backend.Requests);
    }

    [Fact]
    public async Task ScopeError_IsExit4()
    {
        var run = await Draft("- [ ] T1 cs src/A.cs#M x", scopes: ["/ws/nope"]);
        Assert.Equal(4, run.Failure!.ExitCode);
        Assert.Contains("/ws/nope", run.Failure.Message);
        Assert.Empty(run.Backend.Requests);
    }

    [Fact]
    public async Task Prose_IsIgnored_BrokenTaskLine_IsReported()
    {
        var run = await Draft("Here is the plan:\n- [ ] T1 cs src/A.cs#M do x\n- [ ] T2 cs\n```");

        var o = run.Outcome!;
        Assert.Single(ChangeSpecParser.Parse(o.Document).Tasks);
        Assert.Equal(["- [ ] T1 cs src/A.cs#M do x"], TaskLines(o));
        Assert.Equal(["- [ ] T2 cs"], o.DroppedLines);
        Assert.DoesNotContain("Here is the plan", o.Document);
        Assert.DoesNotContain("```", o.Document);
    }

    [Fact]
    public async Task Document_HasTemplateSections()
    {
        var run = await Draft("- [ ] T1 cs src/A.cs#M 값을 두 배로 한다\r\n", request: "주문 취소를 막는다\n둘째 줄");

        Assert.Equal(
            "---\nid: d1\nstatus: draft\n---\n# 주문 취소를 막는다\n\n## 목표\n주문 취소를 막는다\n둘째 줄\n\n" +
            "## 수용 기준\n- AC1: 이 변경이 끝났을 때 참이어야 하는 것\n\n## 작업\n- [ ] T1 cs src/A.cs#M 값을 두 배로 한다\n",
            run.Outcome!.Document);
        Assert.False(run.Outcome.Check.HasErrors);
    }

    [Fact]
    public async Task Title_IsFirstLineCutAt60()
    {
        var request = new string('가', 70) + "\n다음 줄";
        var run = await Draft("- [ ] T1 cs src/A.cs#M x", request: request);
        Assert.Equal("# " + new string('가', 60), run.Outcome!.Document.Split('\n')[4]);
        Assert.Equal(new string('가', 60), ChangeSpecParser.Parse(run.Outcome.Document).Title);
    }

    [Fact]
    public async Task NoTasks_IsExit1()
    {
        var run = await Draft("I cannot do that.\n- [ ] T1 cs");
        Assert.Null(run.Outcome);
        Assert.Equal(1, run.Failure!.ExitCode);
        Assert.Contains("- [ ] T1 cs", run.Failure.Message);
        Assert.False(run.Fs.Directory.Exists("/ws/changes"));
    }

    [Fact]
    public async Task NoGroup_NoImpactSearch()
    {
        var run = await Draft("- [ ] T1 cs src/A.cs#M x", Semantic(new ImpactTarget("T1", "src/B.cs", "Use", "호출")));
        Assert.Empty(run.Impact.Calls);
        Assert.Empty(run.Outcome!.Added);
        Assert.False(run.Outcome.ImpactSkippedForErrors);
    }

    [Fact]
    public async Task GroupSeed_AddsSemanticCallers()
    {
        var run = await Draft("- [ ] T1 cs src/A.cs#M 매개변수를 더한다 [G1]\n- [ ] T2 cs src/C.cs#Run 로그를 남긴다",
            Semantic(new ImpactTarget("T1", "src/B.cs", "Use", "호출")));

        var o = run.Outcome!;
        Assert.Single(run.Impact.Calls);
        Assert.Equal([new AddedImpactTask("T3", "src/B.cs", "Use", "T1")], o.Added);
        Assert.Equal(
        [
            "- [ ] T1 cs src/A.cs#M 매개변수를 더한다 [G1]",
            "- [ ] T3 cs src/B.cs#Use T1의 변경에 맞춰 이 코드를 고친다 (영향 탐색으로 추가) [G1]",
            "- [ ] T2 cs src/C.cs#Run 로그를 남긴다",
        ], TaskLines(o));
        Assert.DoesNotContain("ImpactMissing", Codes(o));
        Assert.False(o.Check.HasErrors);
        Assert.Equal(3, o.Check.Spec.Tasks.Count);
    }

    [Fact]
    public async Task ImpactLines_NumberAfterHighestId()
    {
        var run = await Draft("- [ ] T1 cs src/A.cs#M 매개변수를 더한다 [G1]\n- [ ] T3 cs src/C.cs#Run 맞춘다 [G1]",
            Semantic(new ImpactTarget("T1", "src/B.cs", "Use", "호출")));

        var o = run.Outcome!;
        Assert.Equal([new AddedImpactTask("T4", "src/B.cs", "Use", "T1")], o.Added);
        Assert.Equal("- [ ] T4 cs src/B.cs#Use T1의 변경에 맞춰 이 코드를 고친다 (영향 탐색으로 추가) [G1]", TaskLines(o)[^1]);
        Assert.DoesNotContain("DuplicateId", Codes(o));
    }

    [Fact]
    public async Task AlreadyATask_IsNotAddedAgain()
    {
        var run = await Draft("- [ ] T1 cs src/A.cs#M 매개변수를 더한다 [G1]\n- [ ] T2 cs src/B.cs#Use 맞춘다 [G1]",
            Semantic(new ImpactTarget("T1", "src/B.cs", "Use", "호출")));
        Assert.Empty(run.Outcome!.Added);
        Assert.Equal(2, TaskLines(run.Outcome).Length);
    }

    [Fact]
    public async Task NameBasedResult_AddsNothing()
    {
        var result = new ImpactResult(ImpactMode.Name, new SemanticFailure(SemanticFailureKind.NotRestored),
            [new ImpactTarget("T1", "src/B.cs", "Use", "호출")], []);
        var run = await Draft("- [ ] T1 cs src/A.cs#M 매개변수를 더한다 [G1]", result);

        var o = run.Outcome!;
        Assert.Empty(o.Added);
        Assert.Contains("ImpactMissing", Codes(o));
        Assert.Contains("ImpactNameBased", Codes(o));
        Assert.Single(TaskLines(o));
    }

    [Fact]
    public async Task OutsideMember_Ambiguous_MultiGroup_Sql_AddNothing()
    {
        const string one = "- [ ] T1 cs src/A.cs#M 매개변수를 더한다 [G1]";
        const string two = one + "\n- [ ] T2 cs src/C.cs#Run 맞춘다 [G2]";
        var cases = new (string Answer, ImpactTarget[] Targets)[]
        {
            (one, [new ImpactTarget("T1", "src/B.cs", "B", "멤버 밖")]),
            (one, [new ImpactTarget("T1", "src/A.cs", "Dup", "호출")]),                       // two members named Dup
            (one, [new ImpactTarget("T1", "src/B.cs", "B", "호출")]),                         // a call in the constructor
            (two, [new ImpactTarget("T1", "src/B.cs", "Use", "호출"), new ImpactTarget("T2", "src/B.cs", "Use", "호출")]),
            (one, [new ImpactTarget("T1", "src/B.cs", "Use", "이름 호출(문자열)")]),
            (one, [new ImpactTarget("T1", "", "dbo.usp_X", "EXEC", ImpactTargetKind.SqlObject)]),
        };

        foreach (var (answer, targets) in cases)
        {
            var o = (await Draft(answer, Semantic(targets))).Outcome!;
            Assert.Empty(o.Added);
            Assert.Contains("ImpactMissing", Codes(o));
            Assert.DoesNotContain("영향 탐색으로 추가", o.Document);
        }
    }

    [Fact]
    public async Task CheckErrors_SkipImpact()
    {
        var run = await Draft("- [ ] T1 cs src/A.cs#Nope 고친다 [G1]", Semantic(new ImpactTarget("T1", "src/B.cs", "Use", "호출")));

        var o = run.Outcome!;
        Assert.True(o.ImpactSkippedForErrors);
        Assert.Empty(o.Added);
        Assert.Empty(run.Impact.Calls);
        Assert.True(o.Check.HasErrors);
        Assert.Contains("<!-- spec check: 오류 1, 경고 ", o.Document);
        Assert.Contains("SymbolNotFound", o.Document);

        // The comment sits under "## 작업" without changing what the document parses to; issue lines point at the task.
        var parsed = ChangeSpecParser.Parse(o.Document);
        Assert.Single(parsed.Tasks);
        Assert.Empty(parsed.Issues);
        var error = o.Check.Issues.Single(i => i.Code == "SymbolNotFound");
        Assert.Equal(parsed.Tasks[0].Line, error.Line);
        Assert.StartsWith("- [ ] T1", o.Document.Split('\n')[error.Line - 1]);
    }

    [Fact]
    public async Task AddedLine_KeepsGroupWhenParsed()
    {
        var run = await Draft("- [ ] T1 cs src/A.cs#M 매개변수를 더한다 [G1]", Semantic(new ImpactTarget("T1", "src/B.cs", "Use", "호출")));

        var added = ChangeSpecParser.Parse(run.Outcome!.Document).Tasks.Single(t => t.Id == "T2");
        Assert.Equal("G1", added.Group);
        Assert.Equal("Use", added.Symbol);
        Assert.DoesNotContain("[G1]", added.Request);
        Assert.Contains("(영향 탐색으로 추가)", added.Request);
    }

    [Fact]
    public async Task StaleDraftFile_IsReplacedAndRemoved()
    {
        var fs = Fs();
        fs.AddFile("/ws/.pelly/drafts/d1.md", "stale: not a spec");

        var run = await Draft("- [ ] T1 cs src/A.cs#M x", fs: fs);

        Assert.NotNull(run.Outcome);
        Assert.False(run.Outcome!.Check.HasErrors);
        Assert.False(fs.File.Exists("/ws/.pelly/drafts/d1.md"));
    }

    [Fact]
    public async Task DraftFile_IsRemovedOnCancellation()
    {
        var finder = new FakeImpact(Semantic()) { Throw = new OperationCanceledException() };
        var fs = Fs();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Draft("- [ ] T1 cs src/A.cs#M x [G1]", fs: fs, finder: finder));
        Assert.Single(finder.Calls);
        Assert.False(fs.File.Exists("/ws/.pelly/drafts/d1.md"));
    }

    [Fact]
    public async Task UnmarkedWorkspace_NoModelCall_Exit4()
    {
        var fs = Fs();
        fs.RemoveFile("/ws/App.sln");

        var run = await Draft("- [ ] T1 cs src/A.cs#M x", fs: fs);

        Assert.Null(run.Outcome);
        Assert.Equal(4, run.Failure!.ExitCode);
        Assert.Contains(".git, .svn 또는 *.sln", run.Failure.Message);
        Assert.Empty(run.Backend.Requests);
        Assert.False(fs.File.Exists("/ws/.pelly/drafts/d1.md"));
    }

    [Fact]
    public async Task ScopeWithoutSources_NoModelCall_Exit4()
    {
        var fs = Fs();
        fs.AddFile("/ws/docs/readme.txt", "x");
        fs.AddFile("/ws/db/Bad.sql", "CREATE PROCEDURE (");

        var run = await Draft("- [ ] T1 cs src/A.cs#M x", fs: fs, scopes: ["/ws/docs"]);
        Assert.Equal(4, run.Failure!.ExitCode);
        Assert.Contains("범위에 .cs·.sql 파일이 없습니다: /ws/docs", run.Failure.Message);
        Assert.Empty(run.Backend.Requests);

        run = await Draft("- [ ] T1 cs src/A.cs#M x", fs: fs, scopes: ["/ws/db"]);
        Assert.Equal(4, run.Failure!.ExitCode);
        Assert.Contains("범위에 .cs·.sql 파일이 없습니다: /ws/db", run.Failure.Message);
        Assert.Contains("db/Bad.sql", run.Failure.Message);
        Assert.Empty(run.Backend.Requests);
    }

    [Fact]
    public async Task ImpactLinesOverTaskCap_AddNone_Warn()
    {
        var fs = Fs();
        var many = new StringBuilder("public class D\n{\n");
        for (var i = 1; i <= 20; i++) many.Append($"    public void U{i}() {{ }}\n");
        fs.AddFile("/ws/src/D.cs", many.Append("}\n").ToString());
        var answer = "- [ ] T1 cs src/A.cs#M 매개변수를 더한다 [G1]\n"
            + string.Join("\n", Enumerable.Range(2, 5).Select(n => $"- [ ] T{n} cs src/D.cs#U{n + 14} 로그를 남긴다"));
        var targets = Enumerable.Range(1, 15).Select(n => new ImpactTarget("T1", "src/D.cs", "U" + n, "호출")).ToArray();

        var o = (await Draft(answer, Semantic(targets), fs: fs)).Outcome!;

        Assert.Empty(o.Added);
        Assert.Equal(15, o.ImpactSkippedForCap);
        Assert.Equal(6, TaskLines(o).Length);
        Assert.DoesNotContain("TooManyTasks", Codes(o));
        Assert.Contains("ImpactMissing", Codes(o));
        Assert.False(o.Check.HasErrors);
    }

    [Fact]
    public async Task ImpactLinesWithinTaskCap_AreAdded()
    {
        var run = await Draft("- [ ] T1 cs src/A.cs#M 매개변수를 더한다 [G1]", Semantic(new ImpactTarget("T1", "src/B.cs", "Use", "호출")));
        Assert.Single(run.Outcome!.Added);
        Assert.Equal(0, run.Outcome.ImpactSkippedForCap);
    }

    [Fact]
    public async Task ChangesFolder_Untouched()
    {
        var run = await Draft("- [ ] T1 cs src/A.cs#M 매개변수를 더한다 [G1]", Semantic(new ImpactTarget("T1", "src/B.cs", "Use", "호출")));
        Assert.NotNull(run.Outcome);
        Assert.False(run.Fs.Directory.Exists("/ws/changes"));
    }
}
