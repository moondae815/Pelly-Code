using S6Planning;

public class ScorerTests
{
    private static readonly CorpusIndex Idx = Fx.Index("X/I.cs:cs:M", "X/A.cs:cs:M,N", "X/B.cs:cs:M", "X/s.sql:sql:dbo.usp_s");

    private const string TwoTasks = "- [ ] T1 cs X/I.cs#M a [G1]\n- [ ] T2 cs X/A.cs#M b [G1]";

    [Fact] public void Duplicate_targets_count_once_for_recall()
    {
        var s = Scorer.Score(Fx.Gold('B', TwoTasks), Idx, "A", 1,
            "- [ ] T1 cs X/I.cs#M a\n- [ ] T2 cs X/I.cs#M again\n- [ ] T3 cs X/A.cs#M b");
        Assert.Equal((1.0, 1.0), (s.Recall, s.Precision));
        Assert.True(s.Usable);
    }

    [Fact] public void Optional_missing_is_still_usable()
    {
        var g = Fx.Gold('B', TwoTasks + "\n- [ ] T3 cs X/B.cs#M c", optional: new[] { "T3" });
        Assert.True(Scorer.Score(g, Idx, "A", 1, "- [ ] T1 cs X/I.cs#M a\n- [ ] T2 cs X/A.cs#M b").Usable);
    }

    [Fact] public void Missing_required_is_not_usable()
    {
        var s = Scorer.Score(Fx.Gold('B', TwoTasks), Idx, "A", 1, "- [ ] T1 cs X/I.cs#M a");
        Assert.Equal(0.5, s.Recall); Assert.False(s.Usable);
    }

    [Fact] public void Unknown_member_is_hallucinated_and_not_usable()
    {
        var s = Scorer.Score(Fx.Gold('B', TwoTasks), Idx, "A", 1, TwoTasks + "\n- [ ] T3 cs X/A.cs#Nope c");
        Assert.Equal(1, s.Hallucinated); Assert.False(s.Usable);
    }

    [Fact] public void Forbidden_target_blocks_usable()
    {
        var s = Scorer.Score(Fx.Gold('B', TwoTasks, forbidden: new[] { "X/A.cs#N" }), Idx, "A", 1, TwoTasks + "\n- [ ] T3 cs X/A.cs#N c");
        Assert.Equal(1, s.Forbidden); Assert.False(s.Usable);
    }

    [Fact] public void Caller_before_interface_is_order_violation()
    {
        var g = Fx.Gold('B', TwoTasks, order: new[] { ("T1", "T2") });
        var s = Scorer.Score(g, Idx, "A", 1, "- [ ] T1 cs X/A.cs#M b\n- [ ] T2 cs X/I.cs#M a");
        Assert.Equal(1, s.OrderViolations); Assert.False(s.Usable);
    }

    [Fact] public void Category_A_requires_single_group()
    {
        var g = Fx.Gold('A', TwoTasks, groups: new[] { new[] { "T1", "T2" } });
        var s = Scorer.Score(g, Idx, "A", 1, "- [ ] T1 cs X/I.cs#M a [G1]\n- [ ] T2 cs X/A.cs#M b");
        Assert.False(s.GroupOk); Assert.False(s.Usable);
        Assert.True(Scorer.Score(g, Idx, "A", 1, "- [ ] T1 cs X/I.cs#M a [G2]\n- [ ] T2 cs X/A.cs#M b [G2]").GroupOk);
    }

    [Fact] public void Category_D_extra_task_is_outside_and_not_usable()
    {
        var g = Fx.Gold('D', "- [ ] T1 cs X/A.cs#M a");
        var s = Scorer.Score(g, Idx, "A", 1, "- [ ] T1 cs X/A.cs#M a\n- [ ] T2 cs X/B.cs#M b");
        Assert.Equal(1, s.Outside); Assert.False(s.Usable);
    }

    [Fact] public void Category_B_extra_task_is_outside_but_still_usable()
    {
        var s = Scorer.Score(Fx.Gold('B', TwoTasks), Idx, "A", 1, TwoTasks + "\n- [ ] T3 cs X/B.cs#M c");
        Assert.Equal(1, s.Outside); Assert.True(s.Usable);
    }

    [Fact] public void Example_name_in_output_blocks_usable()
    {
        var s = Scorer.Score(Fx.Gold('B', TwoTasks), Idx, "A", 1, TwoTasks + "\nLike LoanService before.");
        Assert.True(s.ExampleCopy); Assert.False(s.Usable);
    }

    [Fact] public void Sql_target_matches_by_file()
    {
        var g = Fx.Gold('D', "- [ ] T1 sql X/s.sql a");
        Assert.True(Scorer.Score(g, Idx, "A", 1, "- [ ] T1 sql X/s.sql#dbo.usp_s a").Usable);
    }

    [Fact] public void Kind_mismatch_on_matching_target_is_flagged()
    {
        var g = Fx.Gold('D', "- [ ] T1 sql X/s.sql a");
        var s = Scorer.Score(g, Idx, "A", 1, "- [ ] T1 cs X/s.sql#M a");
        Assert.False(s.Usable); Assert.Equal(0, s.Recall);
    }

    [Fact] public void Unparsed_output_is_not_usable()
    {
        var s = Scorer.Score(Fx.Gold('B', TwoTasks), Idx, "A", 1, TwoTasks + "\n- [ ] T3 java X.java#M c");
        Assert.False(s.Parsed); Assert.False(s.Usable);
    }

    [Theory]
    [InlineData(48, 60, 0, 0, VerdictKind.PremiseRejected)]
    [InlineData(48, 60, 1, 1, VerdictKind.Partial)]
    [InlineData(29, 60, 0, 0, VerdictKind.PremiseConfirmed)]
    [InlineData(40, 60, 7, 7, VerdictKind.PremiseConfirmed)]
    public void Verdict_boundaries(int usable, int total, int hallucTotal, int hallucReps, VerdictKind expected) =>
        Assert.Equal(expected, Verdict.Decide(Fx.Scores(usable, total, hallucTotal, hallucReps)));

    [Fact] public void Verdict_partial_when_one_category_below_60() =>
        Assert.Equal(VerdictKind.Partial, Verdict.Decide(Fx.Scores(50, 60, perCategory: new[] { 14, 14, 14, 8 })));

    [Fact] public void Verdict_partial_at_exactly_50_percent() =>
        Assert.Equal(VerdictKind.Partial, Verdict.Decide(Fx.Scores(30, 60)));
}
