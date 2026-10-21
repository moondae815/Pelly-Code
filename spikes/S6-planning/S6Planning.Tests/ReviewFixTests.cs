using S6Planning;

/// <summary>Regression tests for the final-review findings (C1, I1, I2, I3).</summary>
public class ReviewFixTests
{
    private static readonly CorpusIndex Idx = Fx.Index("X/I.cs:cs:M", "X/A.cs:cs:M,N", "X/s.sql:sql:dbo.usp_s");
    private const string Good = "- [ ] T1 cs X/I.cs#M a\n- [ ] T2 cs X/A.cs#M b";

    // C1: task-shaped lines that are not flush-left "- [ ]" must not vanish into ExtraLines.
    [Theory]
    [InlineData("  - [ ] T3 cs X/Nope.cs#Q x")]
    [InlineData("* [ ] T3 cs X/Nope.cs#Q x")]
    [InlineData("+ [ ] T3 cs X/Nope.cs#Q x")]
    [InlineData("1. T3 cs X/Nope.cs#Q x")]
    [InlineData("- T3 cs X/Nope.cs#Q x")]
    public void Task_shaped_line_off_grammar_is_invalid(string line)
    {
        var p = PlanParser.Parse(Good + "\n" + line);
        Assert.Single(p.InvalidLines);
        Assert.False(Scorer.Score(Fx.Gold('B', Good), Idx, "A", 1, Good + "\n" + line).Usable);
    }

    [Fact] public void Plain_prose_is_still_extra() =>
        Assert.Equal(1, PlanParser.Parse(Good + "\nThese two tasks cover the request.").ExtraLines);

    [Fact] public void Gold_body_with_extra_lines_throws() =>
        Assert.Throws<InvalidDataException>(() => GoldLoader.Load(Path.Combine(TestPaths.Fixture("gold-indented"), "c1")));

    // I1: uncommitted gold edits and raws from another gold commit are refused.
    [Fact] public void Gate_refuses_when_cases_have_uncommitted_changes() =>
        Assert.Contains("uncommitted", ApprovalGate.Check(TestPaths.Fixture("gate-approved"), new string('a', 40), casesDirty: true));

    [Fact] public void Score_all_refuses_raw_from_another_gold_commit() =>
        Assert.Throws<InvalidDataException>(() =>
            Report.ScoreAll(TestPaths.Fixture("raw-one"), TestPaths.Cases, TestPaths.Corpus, requiredGoldCommit: new string('a', 40)));

    [Fact] public void Summary_names_gold_commit() =>
        Assert.Contains("goldCommit `abc`", Report.Summary(Fx.Scores(4, 4), Array.Empty<RawRecord>(), goldCommit: "abc"));

    [Fact] public void Raw_record_carries_gold_commit()
    {
        var r = new RawRecord("c", "A", 1, "d", 1, null, false, "o", "stop", 1, 1.0, GoldCommit: "g");
        Assert.Equal("g", RawRecord.FromJson(r.ToJson()).GoldCommit);
    }

    // I2: formatting slips are reported as near misses, separately from invented targets.
    [Theory]
    [InlineData("- [ ] T1 cs `X/I.cs#M` a")]
    [InlineData("- [ ] T1 cs X/I.cs #M a")]
    [InlineData("- [ ] T1 cs X/I.cs#M<T> a")]
    [InlineData("- [ ] T1 sql X/I.cs#M a")]
    public void Format_slip_is_a_near_miss(string line)
    {
        var s = Scorer.Score(Fx.Gold('D', "- [ ] T1 cs X/I.cs#M a"), Idx, "A", 1, line);
        Assert.Equal(1, s.Hallucinated);
        Assert.Equal(1, s.NearMiss);
    }

    [Fact] public void Truly_invented_target_is_not_a_near_miss() =>
        Assert.Equal(0, Scorer.Score(Fx.Gold('D', "- [ ] T1 cs X/I.cs#M a"), Idx, "A", 1, "- [ ] T1 cs X/Nope.cs#M a").NearMiss);

    // I3: kind accuracy is measured on the file, independent of the member.
    [Fact] public void Wrong_kind_on_gold_file_sets_kind_not_ok()
    {
        var s = Scorer.Score(Fx.Gold('D', "- [ ] T1 sql X/s.sql a"), Idx, "A", 1, "- [ ] T1 cs X/s.sql#M a");
        Assert.False(s.KindOk);
    }

    [Fact] public void Right_kind_is_kind_ok() =>
        Assert.True(Scorer.Score(Fx.Gold('D', "- [ ] T1 sql X/s.sql a"), Idx, "A", 1, "- [ ] T1 sql X/s.sql a").KindOk);
}
