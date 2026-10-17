using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Config;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Llm;

public sealed class QwenStreamClientTests
{
    private const string Base = "BASE-PROMPT";
    private const string Marker = "<<<<<<< SEARCH\n";

    private static (QwenStreamClient Client, ScriptedLlmBackend Backend, FakeTimeProvider Time, PellyOptions Opts) Make(ThinkMode mode)
    {
        var opts = new PellyOptions();
        opts.Llm.ThinkMode = mode;
        var backend = new ScriptedLlmBackend();
        var time = new FakeTimeProvider();
        var client = new QwenStreamClient(backend, QwenChatTemplate.LoadDefault(), opts.Llm, opts.Budget, opts.Sampling, time);
        return (client, backend, time, opts);
    }

    [Fact]
    public async Task Retry_Attempt1_SingleCodeRequestWithEmptyThink()
    {
        var (client, backend, _, opts) = Make(ThinkMode.Retry);
        backend.Enqueue("a\n>>>>>>> REPLACE");

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);

        var req = Assert.Single(backend.Requests);
        Assert.Equal(Base + "<think>\n\n</think>\n\n" + Marker, req.RawPrompt);
        Assert.Equal(opts.Sampling.Code, req.Sampling);
        Assert.Equal(2048, req.NumPredict);
        Assert.Equal(["<|im_end|>"], req.Stop);
        Assert.Equal(opts.Llm.NumCtx, req.NumCtx);
        Assert.Equal(opts.Llm.KeepAlive, req.KeepAlive);
        Assert.False(r.ThinkUsed);
        Assert.Null(r.ThinkUsage);
    }

    [Fact]
    public async Task Retry_Attempt2_ThinkThenCode()
    {
        var (client, backend, _, opts) = Make(ThinkMode.Retry);
        backend.Enqueue("plan A\n</think>\n\nignored", evalCount: 40);
        backend.Enqueue("code");

        var r = await client.GenerateEditAsync(Base, 2, CancellationToken.None);

        Assert.Equal(2, backend.Requests.Count);
        var t = backend.Requests[0];
        Assert.Equal(Base + "<think>\n", t.RawPrompt);
        Assert.Equal(opts.Sampling.Think, t.Sampling);
        Assert.Equal(512, t.NumPredict);
        Assert.Equal(["</think>"], t.Stop);
        var c = backend.Requests[1];
        Assert.Equal(Base + "<think>\n" + "plan A" + "\n</think>\n\n" + Marker, c.RawPrompt);
        Assert.Equal(opts.Sampling.Code, c.Sampling);
        Assert.True(r.ThinkUsed);
        Assert.NotNull(r.ThinkUsage);
    }

    [Fact]
    public async Task Always_Attempt1_UsesThink()
    {
        var (client, backend, _, _) = Make(ThinkMode.Always);
        backend.Enqueue("idea\n</think>");
        backend.Enqueue("code");

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);

        Assert.Equal(2, backend.Requests.Count);
        Assert.True(r.ThinkUsed);
    }

    [Fact]
    public async Task Off_Attempt3_NoThink()
    {
        var (client, backend, _, _) = Make(ThinkMode.Off);
        backend.Enqueue("code");

        var r = await client.GenerateEditAsync(Base, 3, CancellationToken.None);

        Assert.Single(backend.Requests);
        Assert.False(r.ThinkUsed);
    }

    [Fact]
    public async Task Think_StopIgnored_CutsAtFirstThinkClose()
    {
        var (client, backend, _, _) = Make(ThinkMode.Always);
        backend.Enqueue("plan A\n</think>\n\nanswer…</think>more", doneReason: "stop");
        backend.Enqueue("code");

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);

        Assert.Equal("plan A", r.ThinkText);
        Assert.False(r.ThinkTruncated);
    }

    [Fact]
    public async Task Think_LengthTruncated_DropsPartialLastLine()
    {
        var (client, backend, _, _) = Make(ThinkMode.Always);
        backend.Enqueue("a\nb\npartial", doneReason: "length", evalCount: 512);
        backend.Enqueue("code");

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);

        Assert.Equal("a\nb", r.ThinkText);
        Assert.True(r.ThinkTruncated);
        Assert.Contains("<think>\na\nb\n</think>\n\n", backend.Requests[1].RawPrompt);
    }

    [Fact]
    public async Task Think_Tokens_FromServerEvalCount()
    {
        var (client, backend, _, _) = Make(ThinkMode.Always);
        backend.Enqueue("x\n", doneReason: "length", evalCount: 512);
        backend.Enqueue("code");

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);

        Assert.Equal(512, r.ThinkTokens);
    }

    [Fact]
    public async Task Output_IsPrefixedWithSearchMarker()
    {
        var (client, backend, _, _) = Make(ThinkMode.Off);
        backend.Enqueue("old\n=======\nnew\n>>>>>>> REPLACE\n");

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);

        Assert.Equal(Marker + "old\n=======\nnew\n>>>>>>> REPLACE\n", r.Output);
    }

    [Fact]
    public async Task Usage_RecordsCachedCountsAndTtft()
    {
        var (client, backend, time, _) = Make(ThinkMode.Off);
        backend.BeforeRespond = _ => { time.Advance(TimeSpan.FromMilliseconds(250)); return Task.CompletedTask; };
        backend.Enqueue("hello world", doneReason: "stop", evalCount: 7, promptEvalCount: 600, promptEvalCachedCount: 512);

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);

        Assert.Equal(600, r.CodeUsage.PromptEvalCount);
        Assert.Equal(512, r.CodeUsage.PromptEvalCachedCount);
        Assert.Equal(7, r.CodeUsage.EvalCount);
        Assert.Equal("stop", r.CodeUsage.DoneReason);
        Assert.Equal(TimeSpan.FromMilliseconds(250), r.CodeUsage.Ttft);
        Assert.Equal(TimeSpan.FromMilliseconds(250), r.CodeUsage.Total);
    }

    [Fact]
    public async Task NeverCancelsBackendStream()
    {
        var (client, backend, _, _) = Make(ThinkMode.Always);
        backend.Enqueue("p\n</think>");
        backend.Enqueue("code");
        using var cts = new CancellationTokenSource();

        await client.GenerateEditAsync(Base, 1, cts.Token);

        Assert.Equal(2, backend.ReceivedTokens.Count);
        Assert.All(backend.ReceivedTokens, t =>
        {
            Assert.Equal(cts.Token, t);
            Assert.False(t.IsCancellationRequested);
        });
    }
}
