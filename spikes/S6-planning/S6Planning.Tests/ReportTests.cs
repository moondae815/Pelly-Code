using S6Planning;

public class ReportTests
{
    [Fact] public void Score_all_reads_raw_tree_and_matches_case_count()
    {
        var s = Report.ScoreAll(TestPaths.Fixture("raw-one"), TestPaths.Cases, TestPaths.Corpus);
        var r = Assert.Single(s);
        Assert.True(r.Usable);
        Assert.Equal(('D', "A", 1), (r.Category, r.Arm, r.Rep));
    }

    [Fact] public void Raw_for_unknown_case_throws() =>
        Assert.Throws<InvalidDataException>(() => Report.ScoreAll(TestPaths.Fixture("raw-one"), TestPaths.Fixture("gate-approved"), TestPaths.Corpus));

    [Fact] public void Summary_contains_verdict_and_category_table()
    {
        var md = Report.Summary(Fx.Scores(usable: 48, total: 60), Array.Empty<RawRecord>());
        Assert.Contains("| A |", md);
        Assert.Contains("PremiseRejected", md);
        Assert.Contains("12/15", md);
    }

    [Fact] public void Summary_compares_arm_C_recall_on_category_A()
    {
        var scores = Fx.Scores(48, 60).Concat(new[]
        {
            new RunScore("a1", 'A', "C", 1, true, 1, 0.5, 0, 0, 0, true, true, 2, false, true),
        }).ToList();
        Assert.Contains("arm C", Report.Summary(scores, Array.Empty<RawRecord>()));
    }

    [Fact] public void Csv_header_follows_record_order()
    {
        var csv = Report.Csv(Fx.Scores(4, 4));
        Assert.StartsWith("CaseId,Category,Arm,Rep,Parsed,Recall,Precision,Hallucinated,Forbidden,OrderViolations,GroupOk,KindOk,Outside,ExampleCopy,Usable,NearMiss\n", csv);
        Assert.Equal(5, csv.Trim().Split('\n').Length);
    }
}
