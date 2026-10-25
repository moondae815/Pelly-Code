using PellyCoder.Core.Impact;
using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Tests.Impact;

public class ImpactCheckTests
{
    private static ChangeSpec Parse(string tasks) =>
        ChangeSpecParser.Parse("---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks);

    private const string Mixed =
        "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 바꾼다 [G1]\n" +
        "- [ ] T2 cs src/App/Repo.cs#Repo.Find 바꾼다 [G1]\n" +
        "- [ ] T3 cs src/App/Other.cs#Other.Run 바꾼다\n" +
        "- [ ] T4 sql db/a.sql 바꾼다 [G1]\n" +
        "- [x] T5 cs src/App/Done.cs#Done.Old 바꾼다 [G1] <!-- run: R-T5 -->\n";

    private static ImpactResult Semantic(params ImpactTarget[] targets) => new(ImpactMode.Semantic, null, targets, []);

    [Fact]
    public void Seeds_are_group_cs_tasks_not_done()
    {
        var spec = Parse(Mixed);

        var group = ImpactCheck.Seeds(spec, allCsTasks: false);
        Assert.Equal(["T1", "T2"], group.Select(s => s.TaskId));
        var t1 = group[0];
        Assert.Equal("src/App/OrderService.cs", t1.Path);
        Assert.Equal("OrderService", t1.ClassName);
        Assert.Equal("GetOrder", t1.Member);
        Assert.Equal(spec.Tasks[0].Line, t1.Line);
        Assert.Equal(TaskKind.CSharp, t1.Kind);

        Assert.Equal(["T1", "T2", "T3"], ImpactCheck.Seeds(spec, allCsTasks: true).Select(s => s.TaskId));
    }

    [Fact]
    public void Seed_path_uses_forward_slashes()
    {
        var spec = Parse("- [ ] T1 cs src\\App\\OrderService.cs#GetOrder 바꾼다 [G1]\n- [ ] T2 cs src/App/Repo.cs#Repo.Find 바꾼다 [G1]\n");

        var seed = ImpactCheck.Seeds(spec, false)[0];
        Assert.Equal("src/App/OrderService.cs", seed.Path);
        Assert.Null(seed.ClassName);
    }

    [Fact]
    public void Target_named_by_any_task_is_not_missing()
    {
        var spec = Parse(Mixed);
        var seeds = ImpactCheck.Seeds(spec, false);

        // Named by T2 (different separator and, where paths ignore case, different case) and by the done task T5.
        var otherCase = PellyCoder.Core.Files.PathComparers.ForOs.Equals("a", "A") ? "SRC\\App\\Repo.cs" : "src\\App\\Repo.cs";
        var covered = ImpactCheck.Issues(spec, seeds, Semantic(
            new ImpactTarget("T1", otherCase, "Find", "호출"),
            new ImpactTarget("T1", "src/App/Done.cs", "Old", "호출")));
        Assert.Empty(covered);

        var missing = ImpactCheck.Issues(spec, seeds, Semantic(new ImpactTarget("T1", "src/App/Controller.cs", "Detail", "호출")));
        var issue = Assert.Single(missing);
        Assert.Equal(new SpecIssue(spec.Tasks[0].Line, SpecIssueSeverity.Warning, "ImpactMissing",
            "작업 T1의 영향 대상이 명세에 없습니다: src/App/Controller.cs#Detail (호출)"), issue);

        // Same path, other member: still missing.
        var otherMember = ImpactCheck.Issues(spec, seeds, Semantic(new ImpactTarget("T2", "src/App/Repo.cs", "Save", "재정의")));
        Assert.Equal("작업 T2의 영향 대상이 명세에 없습니다: src/App/Repo.cs#Save (재정의)", Assert.Single(otherMember).Message);
        Assert.Equal(spec.Tasks[1].Line, otherMember[0].Line);
    }

    [Fact]
    public void Covers_matches_path_and_member_of_cs_tasks_only()
    {
        var spec = Parse(Mixed);

        Assert.True(ImpactCheck.Covers(spec.Tasks[0], new ImpactTarget("T2", "src/App/OrderService.cs", "GetOrder", "호출")));
        Assert.False(ImpactCheck.Covers(spec.Tasks[0], new ImpactTarget("T2", "src/App/OrderService.cs", "Cancel", "호출")));
        Assert.False(ImpactCheck.Covers(spec.Tasks[3], new ImpactTarget("T1", "db/a.sql", "a", "호출")));
    }

    [Fact]
    public void Name_mode_always_warns_ImpactNameBased()
    {
        var spec = Parse(Mixed);
        var result = new ImpactResult(ImpactMode.Name, new SemanticFailure(SemanticFailureKind.LowResolution, 0.028), [], []);

        var issue = Assert.Single(ImpactCheck.Issues(spec, ImpactCheck.Seeds(spec, false), result));
        Assert.Equal(new SpecIssue(0, SpecIssueSeverity.Warning, "ImpactNameBased", "영향 탐색: 이름 기준(부정확): 의미 분석 실패 — 해석률 2.8%"), issue);
    }

    [Fact]
    public void Incomplete_result_warns()
    {
        var spec = Parse(Mixed);
        var result = new ImpactResult(ImpactMode.Semantic, null, [], [], Incomplete: true);

        var issue = Assert.Single(ImpactCheck.Issues(spec, ImpactCheck.Seeds(spec, false), result));
        Assert.Equal(new SpecIssue(0, SpecIssueSeverity.Warning, "ImpactIncomplete", "영향 탐색: 일부 폴더·파일을 읽지 못해 결과가 불완전합니다"), issue);
    }

    [Fact]
    public void Unsupported_seed_warns()
    {
        var spec = Parse(Mixed);
        var result = new ImpactResult(ImpactMode.Semantic, null, [], ["T2"]);

        var issue = Assert.Single(ImpactCheck.Issues(spec, ImpactCheck.Seeds(spec, false), result));
        Assert.Equal(new SpecIssue(spec.Tasks[1].Line, SpecIssueSeverity.Warning, "ImpactUnsupported", "작업 T2: 영향 탐색은 메서드·속성만 봅니다."), issue);
    }
}
