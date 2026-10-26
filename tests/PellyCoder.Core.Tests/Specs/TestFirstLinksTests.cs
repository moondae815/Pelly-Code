using PellyCoder.Core.Specs;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.Specs;

public sealed class TestFirstLinksTests
{
    private static SpecTask Task(string id, TaskKind kind, bool done, params string[] acs) =>
        new(id, kind, "src/A.cs", "A.M", "req", acs, null, done, null, null, 1);

    private static ChangeSpec Spec(AcceptanceCriterion[] criteria, params SpecTask[] tasks) =>
        new("x", SpecStatus.Draft, "t", criteria, tasks, []);

    private static AcceptanceCriterion Ac(string id, string? test, params string[] breaks) =>
        new(id, "text", test, breaks.Length == 0 ? null : breaks);

    [Fact]
    public void Links_FirstAndLastLinkedCsTasks()
    {
        var spec = Spec([Ac("AC1", "T.A")],
            Task("T1", TaskKind.CSharp, false, "AC1"), Task("T2", TaskKind.Sql, false, "AC1"), Task("T3", TaskKind.CSharp, false, "AC1"));
        var links = TestFirstLinks.Build(spec);

        Assert.Equal(["T1", "T3"], Assert.Single(links).LinkedTaskIds);
        Assert.Equal(TestCheck.Red, TestFirstLinks.For(links, "T1")!.Checks["T.A"]);
        Assert.Equal(TestCheck.Green, TestFirstLinks.For(links, "T3")!.Checks["T.A"]);
        Assert.Null(TestFirstLinks.For(links, "T2"));
    }

    [Fact]
    public void Links_SingleLinkedTask_RedAndGreen()
    {
        var links = TestFirstLinks.Build(Spec([Ac("AC1", "T.A")], Task("T1", TaskKind.CSharp, false, "AC1")));
        Assert.Equal(TestCheck.Red | TestCheck.Green, TestFirstLinks.For(links, "T1")!.Checks["T.A"]);
    }

    [Fact]
    public void Links_SqlTaskIsNotLinked()
    {
        var links = TestFirstLinks.Build(Spec([Ac("AC1", "T.A")], Task("T1", TaskKind.Sql, false, "AC1")));
        Assert.Empty(Assert.Single(links).LinkedTaskIds);
    }

    [Fact]
    public void Links_CriterionWithoutTest_IsSkipped()
    {
        Assert.Empty(TestFirstLinks.Build(Spec([Ac("AC1", null)], Task("T1", TaskKind.CSharp, false, "AC1"))));
    }

    [Fact]
    public void Links_TaskLastForAc1FirstForAc2()
    {
        var spec = Spec([Ac("AC1", "T.One"), Ac("AC2", "T.Two")],
            Task("T1", TaskKind.CSharp, false, "AC1"), Task("T2", TaskKind.CSharp, false, "AC1", "AC2"), Task("T3", TaskKind.CSharp, false, "AC2"));
        var checks = TestFirstLinks.For(TestFirstLinks.Build(spec), "T2")!.Checks;

        Assert.Equal(TestCheck.Green, checks["T.One"]);
        Assert.Equal(TestCheck.Red, checks["T.Two"]);
    }

    [Fact]
    public void Links_MiddleTask_HasNoneRole()
    {
        var spec = Spec([Ac("AC1", "T.A")],
            Task("T1", TaskKind.CSharp, false, "AC1"), Task("T2", TaskKind.CSharp, false, "AC1"), Task("T3", TaskKind.CSharp, false, "AC1"));
        Assert.Equal(TestCheck.None, TestFirstLinks.For(TestFirstLinks.Build(spec), "T2")!.Checks["T.A"]);
    }

    [Fact]
    public void Links_SameTestInTwoAcs_RolesAreOred()
    {
        var spec = Spec([Ac("AC1", "T.A"), Ac("AC2", "T.A")],
            Task("T1", TaskKind.CSharp, false, "AC1"), Task("T2", TaskKind.CSharp, false, "AC1", "AC2"), Task("T3", TaskKind.CSharp, false, "AC2"));
        Assert.Equal(TestCheck.Red | TestCheck.Green, TestFirstLinks.For(TestFirstLinks.Build(spec), "T2")!.Checks["T.A"]);
    }

    [Fact]
    public void Links_DoneTasksIncluded()
    {
        var spec = Spec([Ac("AC1", "T.A")], Task("T1", TaskKind.CSharp, true, "AC1"), Task("T2", TaskKind.CSharp, false, "AC1"));
        Assert.Equal(["T1", "T2"], Assert.Single(TestFirstLinks.Build(spec)).LinkedTaskIds);
    }

    [Fact]
    public void Links_BreaksCarried()
    {
        var spec = Spec([Ac("AC1", "T.A", "T.Old", "T.Older")], Task("T1", TaskKind.CSharp, false, "AC1"));
        var links = TestFirstLinks.Build(spec);
        Assert.Equal(["T.Old", "T.Older"], Assert.Single(links).Breaks);
        Assert.Equal(["T.Old", "T.Older"], TestFirstLinks.For(links, "T1")!.Breaks);
    }
}
