using PellyCoder.Core.Llm;
using S6Planning;

public class RunnerTests
{
    private static readonly string A40 = new('a', 40);

    [Fact] public void Gate_refuses_without_approval_file() =>
        Assert.Contains("APPROVED.json", ApprovalGate.Check(TestPaths.Fixture("gate-missing"), A40));

    [Fact] public void Gate_refuses_on_commit_mismatch() =>
        Assert.Contains("goldCommit", ApprovalGate.Check(TestPaths.Fixture("gate-approved"), new string('b', 40)));

    [Fact] public void Gate_refuses_when_any_gold_is_draft() =>
        Assert.Contains("draft", ApprovalGate.Check(TestPaths.Fixture("gate-one-draft"), A40));

    [Fact] public void Gate_passes_when_all_approved_and_commit_matches() =>
        Assert.Null(ApprovalGate.Check(TestPaths.Fixture("gate-approved"), A40));

    [Fact] public void Real_cases_are_refused_for_a_foreign_commit() =>
        Assert.NotNull(ApprovalGate.Check(TestPaths.Cases, A40));

    [Theory]
    [InlineData("a7eb95c53bcf96b4bdd008d0fab4a5dac88047d9c1a7a9ab88ed453423fbd87c", true)]
    [InlineData("sha256:a7eb95c53bcf96b4", false)]
    [InlineData("cd2692a833e66c4c98991b67e9fbaa0bb15a93285baac9240c022f2f40075b6d", false)]
    [InlineData(null, false)]
    public void Digest_must_be_field_model(string? d, bool ok) => Assert.Equal(ok, Runner.CheckDigest(d) is null);

    [Fact] public void Stage2_closes_truncated_think()
    {
        var t = QwenChatTemplate.LoadDefault();
        Assert.EndsWith("half thought" + t.ThinkClose, Runner.Stage2Prompt("P" + t.ThinkOpen, "half thought", t));
    }

    [Fact] public void Generate_body_carries_sampling_and_budget()
    {
        var body = Runner.GenerateBody("qwen3.6:35b", "PROMPT", Runner.CodeSampling, 1024, new[] { "</think>" });
        var o = body["options"]!;
        Assert.Equal(("PROMPT", true, false), ((string)body["prompt"]!, (bool)body["raw"]!, (bool)body["stream"]!));
        Assert.Equal((0.1, 0.9, 20, 1.05, 0.0, 1024, 8192),
            ((double)o["temperature"]!, (double)o["top_p"]!, (int)o["top_k"]!, (double)o["repeat_penalty"]!, (double)o["presence_penalty"]!, (int)o["num_predict"]!, (int)o["num_ctx"]!));
    }

    [Fact] public void Raw_record_round_trips()
    {
        var r = new RawRecord("c", "B", 2, "d", 100, "think", true, "out", "stop", 50, 1.5);
        Assert.Equal(r, RawRecord.FromJson(r.ToJson()));
    }
}
