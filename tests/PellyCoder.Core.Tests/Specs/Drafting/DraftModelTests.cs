using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Specs.Drafting;

namespace PellyCoder.Core.Tests.Specs.Drafting;

public sealed class DraftModelTests
{
    private static readonly QwenChatTemplate Template = QwenChatTemplate.LoadDefault();

    private static (DraftModel Model, FakeDraftBackend Backend) Make(params (string, string?)[] script)
    {
        var backend = new FakeDraftBackend(script);
        return (new DraftModel(backend, Template, TimeProvider.System), backend);
    }

    private static string S6System()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "spikes", "S6-planning", "S6Planning", "PlanPrompt.cs");
            if (!File.Exists(path)) continue;
            var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
            var sysAt = Array.FindIndex(lines, l => l.Contains("string System", StringComparison.Ordinal));
            var open = Array.FindIndex(lines, sysAt, l => l.Trim() == "\"\"\"");
            var close = Array.FindIndex(lines, open + 1, l => l.Trim() == "\"\"\";");
            var indent = lines[close].Length - lines[close].TrimStart().Length; // the closing line's indentation is removed from every line
            return string.Join("\n", lines[(open + 1)..close].Select(l => l.Length == 0 ? l : l[indent..]));
        }
        throw new FileNotFoundException("PlanPrompt.cs");
    }

    [Fact]
    public void System_IsS6PromptVerbatim() => Assert.Equal(DraftPrompt.System, S6System());

    [Fact]
    public void User_IsIndexThenRequest() => Assert.Equal("Index:\nIDX\nRequest:\nREQ", DraftPrompt.User("REQ", "IDX"));

    [Fact]
    public async Task TwoStages_UseS6Settings()
    {
        var (model, backend) = Make(("thought", "stop"), ("- [ ] T1 sql a.sql x", "stop"));
        var call = await model.RunAsync("req", "idx", CancellationToken.None);

        Assert.Equal(2, backend.Requests.Count);
        var (r1, r2) = (backend.Requests[0], backend.Requests[1]);
        Assert.Equal(new SamplingOptions(0.6, 0.95, 20, 1.0, 0.0), r1.Sampling);
        Assert.Equal(2048, r1.NumPredict);
        Assert.Equal([Template.ThinkClose], r1.Stop);
        Assert.Equal(new SamplingOptions(0.1, 0.9, 20, 1.05, 0.0), r2.Sampling);
        Assert.Equal(1024, r2.NumPredict);
        Assert.Equal([Template.EndOfTurn], r2.Stop);
        Assert.All(backend.Requests, r =>
        {
            Assert.Equal(8192, r.NumCtx);
            Assert.Equal("10m", r.KeepAlive);
        });
        Assert.Equal(Template.Build(DraftPrompt.System, "Index:\nidx\nRequest:\nreq") + Template.ThinkOpen, r1.RawPrompt);
        Assert.Equal(r1.RawPrompt, call.Stage1Prompt);
        Assert.Equal("- [ ] T1 sql a.sql x", call.Answer);
        Assert.False(call.ThinkTruncated);
    }

    [Fact]
    public async Task Stage2_ConcatenatesThinkVerbatim()
    {
        var (model, backend) = Make((" a\nb  ", "stop"), ("x", "stop"));
        var call = await model.RunAsync("req", "idx", CancellationToken.None);

        Assert.Equal(" a\nb  ", call.Think);
        Assert.Equal(backend.Requests[0].RawPrompt + " a\nb  " + Template.ThinkClose, backend.Requests[1].RawPrompt);
    }

    [Fact]
    public async Task TruncatedThink_IsReportedAndKept()
    {
        var (model, backend) = Make(("cut off mid sen", "length"), ("x", "stop"));
        var call = await model.RunAsync("req", "idx", CancellationToken.None);

        Assert.True(call.ThinkTruncated);
        Assert.Equal("cut off mid sen", call.Think);
        Assert.EndsWith("cut off mid sen" + Template.ThinkClose, backend.Requests[1].RawPrompt);
    }

    [Fact]
    public async Task CountInput_MatchesTokenCounter()
    {
        var (model, _) = Make(("t", "stop"), ("x", "stop"));
        var prompt = Template.Build(DraftPrompt.System, DraftPrompt.User("req", "idx")) + Template.ThinkOpen;

        Assert.Equal(QwenTokenCounter.Shared.Count(prompt), model.CountInput("req", "idx"));
        Assert.Equal(model.CountInput("req", "idx"), (await model.RunAsync("req", "idx", CancellationToken.None)).InputTokens);
    }
}
