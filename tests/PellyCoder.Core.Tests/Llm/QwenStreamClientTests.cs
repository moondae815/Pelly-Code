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
    public async Task GenerateEdit_DefaultPrefix_IsSearchMarker()
    {
        var (client, backend, _, _) = Make(ThinkMode.Off);
        backend.Enqueue("a\n>>>>>>> REPLACE");

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);

        Assert.EndsWith(Marker, Assert.Single(backend.Requests).RawPrompt);
        Assert.StartsWith(Marker, r.Output);
    }

    [Fact]
    public async Task GenerateEdit_CustomPrefix_UsedInPromptAndOutput()
    {
        var (client, backend, _, _) = Make(ThinkMode.Off);
        backend.Enqueue("using System;");

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None, answerPrefix: "### USINGS\n");

        Assert.EndsWith("### USINGS\n", Assert.Single(backend.Requests).RawPrompt);
        Assert.StartsWith("### USINGS\n", r.Output);
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

    /// <summary>Makes the first chunk arrive after <paramref name="seconds"/> of fake time, one second at a time.</summary>
    private static void DelayFirstChunk(ScriptedLlmBackend backend, FakeTimeProvider time, int seconds) =>
        backend.BeforeRespond = _ =>
        {
            for (var i = 0; i < seconds; i++)
                time.Advance(TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        };

    [Fact]
    public async Task SlowFirstToken_ShowsWaitingStatus_AndRecordsQueue()
    {
        var (client, backend, time, _) = Make(ThinkMode.Off);
        backend.Enqueue("code", loadDuration: TimeSpan.FromSeconds(1), promptEvalDuration: TimeSpan.FromSeconds(0.5));
        DelayFirstChunk(backend, time, 6);
        var waits = new List<double>();

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None, e => waits.Add(e.TotalSeconds));

        // at the 6 s tie the timer fires before the chunk is consumed, so 6 is reported too
        Assert.Equal([3.0, 4.0, 5.0, 6.0], waits);
        Assert.Equal(TimeSpan.FromSeconds(6), r.CodeUsage.Ttft);
        Assert.Equal(TimeSpan.FromSeconds(4.5), r.CodeUsage.Queue);
    }

    [Fact]
    public async Task WaitingCallbackThatThrowsCancellation_DoesNotBreakGeneration()
    {
        var (client, backend, time, _) = Make(ThinkMode.Off);
        backend.Enqueue("code");
        DelayFirstChunk(backend, time, 5);
        var calls = 0;

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None, _ =>
        {
            calls++;
            throw new OperationCanceledException();
        });

        Assert.True(calls > 0);
        Assert.Contains("code", r.Output);
    }

    [Fact]
    public async Task SlowFirstToken_SummaryTtftExcludesQueue()
    {
        var (client, backend, time, opts) = Make(ThinkMode.Off);
        backend.Enqueue("code", loadDuration: TimeSpan.FromSeconds(1), promptEvalDuration: TimeSpan.FromSeconds(0.5));
        DelayFirstChunk(backend, time, 6);
        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);
        var request = new PellyCoder.Core.Fsm.RunRequest("a.cs", null, "M", null, "req", true, false, null);
        var ctx = new PellyCoder.Core.Fsm.RunContext("id", request, opts) { Generation = r };

        var summary = PellyCoder.Core.Logging.RunLogger.Summarize(ctx);

        Assert.Equal(1.5, summary.TtftSeconds, 6);
        Assert.Equal(4.5, summary.QueueSeconds, 6);
    }

    [Fact]
    public async Task FastFirstToken_NoWaitingCallback()
    {
        var (client, backend, time, _) = Make(ThinkMode.Off);
        backend.Enqueue("code");
        DelayFirstChunk(backend, time, 1);
        var waits = new List<double>();

        await client.GenerateEditAsync(Base, 1, CancellationToken.None, e => waits.Add(e.TotalSeconds));
        time.Advance(TimeSpan.FromSeconds(30)); // the timer must be gone after the request

        Assert.Empty(waits);
    }

    [Fact]
    public async Task NoDurationsReported_QueueIsZero_TtftKept()
    {
        var (client, backend, time, _) = Make(ThinkMode.Off);
        backend.Enqueue("code");
        DelayFirstChunk(backend, time, 6);

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, r.CodeUsage.Queue);
        Assert.Equal(TimeSpan.FromSeconds(6), r.CodeUsage.Ttft);
    }

    [Fact]
    public async Task Queue_NeverNegative()
    {
        var (client, backend, time, _) = Make(ThinkMode.Off);
        backend.Enqueue("code", loadDuration: TimeSpan.FromSeconds(5), promptEvalDuration: TimeSpan.FromSeconds(5));
        DelayFirstChunk(backend, time, 2);

        var r = await client.GenerateEditAsync(Base, 1, CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, r.CodeUsage.Queue);
    }

    [Fact]
    public async Task Waiting_CountsThinkAndCodeRequestsSeparately()
    {
        var (client, backend, time, _) = Make(ThinkMode.Always);
        backend.Enqueue("p\n</think>");
        backend.Enqueue("code");
        DelayFirstChunk(backend, time, 3);
        var waits = new List<double>();

        await client.GenerateEditAsync(Base, 1, CancellationToken.None, e => waits.Add(e.TotalSeconds));

        Assert.Equal([3.0, 3.0], waits);
    }

    [Fact]
    public async Task WaitingStops_WhenRequestThrows()
    {
        var (client, backend, time, _) = Make(ThinkMode.Off);
        backend.Enqueue("code");
        backend.BeforeRespond = _ =>
        {
            time.Advance(TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("boom");
        };
        var waits = new List<double>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateEditAsync(Base, 1, CancellationToken.None, e => waits.Add(e.TotalSeconds)));
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal([3.0], waits);
    }
}
