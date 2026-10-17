using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Llm;

public class OllamaBackendTests
{
    private const string Model = "qwen3.6:35b-a3b-coding-nvfp4";

    private static readonly LlmRequest Request = new(
        "<|im_start|>user\nhi<|im_end|>\n<|im_start|>assistant\n",
        new SamplingOptions(0.6, 0.95, 20, 1.0, 0.0),
        NumPredict: 6, Stop: ["</think>"], NumCtx: 8192, KeepAlive: "30m");

    private static string Fixture(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PellyCoder.sln")))
                return File.ReadAllText(Path.Combine(dir.FullName, "tests", "fixtures", "ollama", name));
        throw new DirectoryNotFoundException("PellyCoder.sln not found above test binaries.");
    }

    private static (OllamaBackend Backend, FakeTimeProvider Time) Create(StubHttpHandler handler, LlmOptions? options = null)
    {
        options ??= new LlmOptions { Endpoint = "http://ollama.test:11434", Model = Model };
        var time = new FakeTimeProvider();
        var http = new HttpClient(handler);
        return (new OllamaBackend(http, options, time), time);
    }

    private static async Task<List<LlmChunk>> Collect(IAsyncEnumerable<LlmChunk> source)
    {
        var list = new List<LlmChunk>();
        await foreach (var c in source)
            list.Add(c);
        return list;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached.");
            await Task.Delay(5);
        }
    }

    private static HttpResponseMessage Ndjson(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/x-ndjson") };

    private static StubHttpHandler StreamHandler(string text) =>
        new StubHttpHandler().On("/api/generate", (_, _) => Task.FromResult(Ndjson(text)));

    [Fact]
    public async Task Generate_SendsRawRequestBody()
    {
        var handler = StreamHandler(Fixture("generate-stream.ndjson"));
        var (backend, _) = Create(handler);

        await Collect(backend.GenerateAsync(Request, CancellationToken.None));

        var call = Assert.Single(handler.Calls);
        Assert.Equal("POST", call.Method);
        using var doc = JsonDocument.Parse(call.Body);
        var root = doc.RootElement;
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.Equal(Request.RawPrompt, root.GetProperty("prompt").GetString());
        Assert.True(root.GetProperty("raw").GetBoolean());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("30m", root.GetProperty("keep_alive").GetString());
        var o = root.GetProperty("options");
        Assert.Equal(8192, o.GetProperty("num_ctx").GetInt32());
        Assert.Equal(6, o.GetProperty("num_predict").GetInt32());
        Assert.Equal(0.6, o.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, o.GetProperty("top_p").GetDouble());
        Assert.Equal(20, o.GetProperty("top_k").GetInt32());
        Assert.Equal(1.0, o.GetProperty("repeat_penalty").GetDouble());
        Assert.Equal(0.0, o.GetProperty("presence_penalty").GetDouble());
        Assert.Equal("</think>", Assert.Single(o.GetProperty("stop").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Generate_ParsesRecordedStream()
    {
        var recorded = Fixture("generate-stream.ndjson");
        var expectedText = string.Concat(recorded.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("response").GetString()));
        var (backend, _) = Create(StreamHandler(recorded));

        var chunks = await Collect(backend.GenerateAsync(Request, CancellationToken.None));

        Assert.Equal(expectedText, string.Concat(chunks.Select(c => c.Text)));
        Assert.NotEmpty(expectedText);
        Assert.All(chunks.SkipLast(1), c => Assert.False(c.Done));
        var last = chunks[^1];
        Assert.True(last.Done);
        Assert.Equal(13, last.PromptEvalCount);
        Assert.Equal(0, last.PromptEvalCachedCount);
        Assert.Equal(6, last.EvalCount);
        Assert.Equal("length", last.DoneReason);
        Assert.Equal(TimeSpan.FromTicks(1738253375 / 100), last.PromptEvalDuration);
    }

    [Fact]
    public async Task Generate_IgnoresUnknownFieldsAndBlankLines()
    {
        var text = "\n"
                   + """{"response":"a","future_field":{"x":1},"done":false}""" + "\n\n"
                   + """{"response":"b","done":true,"done_reason":"stop","brand_new":[1,2]}""" + "\n";
        var (backend, _) = Create(StreamHandler(text));

        var chunks = await Collect(backend.GenerateAsync(Request, CancellationToken.None));

        Assert.Equal(["a", "b"], chunks.Select(c => c.Text));
        Assert.Null(chunks[0].PromptEvalCount);
        Assert.Null(chunks[0].DoneReason);
        Assert.Equal("stop", chunks[1].DoneReason);
    }

    [Fact]
    public async Task Generate_ConnectFailure_ThrowsLlmUnavailable()
    {
        var handler = new StubHttpHandler().On("/api/generate", (_, _) => throw new HttpRequestException("refused"));
        var (backend, _) = Create(handler);

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(
            () => Collect(backend.GenerateAsync(Request, CancellationToken.None)));
        Assert.Contains("refused", ex.Message + ex.InnerException?.Message);
    }

    [Fact]
    public async Task Generate_NoFirstLineWithinFirstTokenTimeout_RetriesOnceThenThrows()
    {
        var handler = new StubHttpHandler().On("/api/generate", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ndjson("");
        });
        var (backend, time) = Create(handler);

        var run = Collect(backend.GenerateAsync(Request, CancellationToken.None));
        await WaitUntil(() => handler.CountFor("/api/generate") == 1);
        time.Advance(TimeSpan.FromSeconds(120));
        await WaitUntil(() => handler.CountFor("/api/generate") == 2);
        time.Advance(TimeSpan.FromSeconds(120));

        await Assert.ThrowsAsync<LlmUnavailableException>(() => run);
        Assert.Equal(2, handler.CountFor("/api/generate"));
    }

    [Fact]
    public async Task Generate_ColdLoadSlowerThanConnectTimeout_IsNotAFailure()
    {
        // A 25 s cold model load (S1) must not trip the 3 s connect timeout.
        var pipe = new ChannelStream();
        var handler = new StubHttpHandler().On("/api/generate", (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe) }));
        var (backend, time) = Create(handler);

        var run = Collect(backend.GenerateAsync(Request, CancellationToken.None));
        await WaitUntil(() => handler.CountFor("/api/generate") == 1);
        time.Advance(TimeSpan.FromSeconds(30));
        pipe.WriteLine("""{"response":"ok","done":true,"done_reason":"stop"}""");
        pipe.Complete();

        var chunks = await run;
        Assert.Equal("ok", Assert.Single(chunks).Text);
        Assert.Equal(1, handler.CountFor("/api/generate"));
    }

    [Fact]
    public async Task Generate_IdleMidStream_ThrowsLlmUnavailable()
    {
        var pipe = new ChannelStream();
        var handler = new StubHttpHandler().On("/api/generate", (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe) }));
        var (backend, time) = Create(handler);
        var received = new List<LlmChunk>();

        var run = Task.Run(async () =>
        {
            await foreach (var c in backend.GenerateAsync(Request, CancellationToken.None))
                received.Add(c);
        }, TestContext.Current.CancellationToken);
        pipe.WriteLine("""{"response":"a","done":false}""");
        await WaitUntil(() => received.Count == 1);
        time.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<LlmUnavailableException>(() => run);
        Assert.Equal(1, handler.CountFor("/api/generate")); // no re-send mid-stream
    }

    [Fact]
    public async Task Generate_Cancellation_StopsEnumeration()
    {
        var pipe = new ChannelStream();
        var handler = new StubHttpHandler().On("/api/generate", (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe) }));
        var (backend, _) = Create(handler);
        using var cts = new CancellationTokenSource();
        var received = new List<LlmChunk>();

        var run = Task.Run(async () =>
        {
            await foreach (var c in backend.GenerateAsync(Request, cts.Token))
                received.Add(c);
        }, TestContext.Current.CancellationToken);
        pipe.WriteLine("""{"response":"a","done":false}""");
        await WaitUntil(() => received.Count == 1);
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => run);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<LlmUnavailableException>(ex);
    }

    [Fact]
    public async Task Generate_Http404_ThrowsLlmUnavailable_WithModelHint()
    {
        var handler = new StubHttpHandler().OnJson("/api/generate", $$"""{"error":"model '{{Model}}' not found"}""", HttpStatusCode.NotFound);
        var (backend, _) = Create(handler);

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(
            () => Collect(backend.GenerateAsync(Request, CancellationToken.None)));
        Assert.Contains(Model, ex.Message);
        Assert.Contains("404", ex.Message);
        Assert.Contains("not found", ex.Message);
        Assert.Equal(1, handler.CountFor("/api/generate"));
    }

    private static StubHttpHandler HealthyServer(string psModels = $$"""[{"name":"{{Model}}","model":"{{Model}}"}]""") =>
        new StubHttpHandler()
            .OnJson("/api/version", """{"version":"0.40.0"}""")
            .OnJson("/api/tags", $$"""{"models":[{"name":"other:1b","model":"other:1b"},{"name":"{{Model}}","model":"{{Model}}"}]}""")
            .OnJson("/api/ps", $$"""{"models":{{psModels}}}""")
            .OnJson("/api/show", Fixture("show.json"));

    [Fact]
    public async Task Status_ReadsVersionTagsPsShow()
    {
        var handler = HealthyServer();
        var (backend, _) = Create(handler);

        var s = await backend.GetStatusAsync(CancellationToken.None);

        Assert.True(s.Reachable);
        Assert.Equal("0.40.0", s.Version);
        Assert.True(s.ModelInstalled);
        Assert.True(s.ModelLoaded);
        Assert.Equal("qwen3.5", s.Renderer);
        Assert.Null(s.Error);
        Assert.Contains(Model, handler.Calls.Single(c => c.Path == "/api/show").Body);
    }

    [Fact]
    public async Task Status_InstalledButNotLoaded()
    {
        var (backend, _) = Create(HealthyServer(psModels: "[]"));

        var s = await backend.GetStatusAsync(CancellationToken.None);

        Assert.True(s.ModelInstalled);
        Assert.False(s.ModelLoaded);
    }

    [Fact]
    public async Task Status_ModelWithoutTag_MatchesLatest()
    {
        var handler = new StubHttpHandler()
            .OnJson("/api/version", """{"version":"0.40.0"}""")
            .OnJson("/api/tags", """{"models":[{"name":"mymodel:latest","model":"mymodel:latest"}]}""")
            .OnJson("/api/ps", """{"models":[]}""")
            .OnJson("/api/show", Fixture("show.json"));
        var (backend, _) = Create(handler, new LlmOptions { Endpoint = "http://ollama.test:11434", Model = "mymodel" });

        var s = await backend.GetStatusAsync(CancellationToken.None);

        Assert.True(s.ModelInstalled);
    }

    [Fact]
    public async Task Status_Unreachable_ReturnsReachableFalseWithError()
    {
        var handler = new StubHttpHandler().On("/api/version", (_, _) => throw new HttpRequestException("connection refused"));
        var (backend, _) = Create(handler);

        var s = await backend.GetStatusAsync(CancellationToken.None);

        Assert.False(s.Reachable);
        Assert.Contains("connection refused", s.Error);
        Assert.False(s.ModelInstalled);
        Assert.False(s.ModelLoaded);
    }

    [Fact]
    public async Task Status_TimesOutAfterConnectTimeout_ReturnsUnreachable()
    {
        var handler = new StubHttpHandler().On("/api/version", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StubHttpHandler.JsonResponse("{}");
        });
        var (backend, time) = Create(handler);

        var run = backend.GetStatusAsync(CancellationToken.None);
        await WaitUntil(() => handler.CountFor("/api/version") == 1);
        time.Advance(TimeSpan.FromSeconds(3));
        var s = await run;

        Assert.False(s.Reachable);
        Assert.NotNull(s.Error);
    }

    [Fact]
    public async Task Status_LaterCallFailure_KeepsReachableAndReportsError()
    {
        var handler = HealthyServer();
        handler.On("/api/tags", (_, _) => throw new HttpRequestException("tags broke"));
        var (backend, _) = Create(handler);

        var s = await backend.GetStatusAsync(CancellationToken.None);

        Assert.True(s.Reachable);
        Assert.Equal("0.40.0", s.Version);
        Assert.Contains("tags broke", s.Error);
    }

    [Fact]
    public async Task Headers_AreSentOnEveryRequest()
    {
        var handler = HealthyServer().On("/api/generate", (_, _) => Task.FromResult(Ndjson("""{"response":"a","done":true}""")))
            .OnJson("/api/chat", """{"prompt_eval_count":5}""");
        var options = new LlmOptions
        {
            Endpoint = "http://ollama.test:11434", Model = Model,
            Headers = new() { ["X-Api-Key"] = "secret", ["X-Team"] = "pelly" },
        };
        var (backend, _) = Create(handler, options);

        await Collect(backend.GenerateAsync(Request, CancellationToken.None));
        await backend.GetStatusAsync(CancellationToken.None);
        await backend.ChatPromptTokenCountAsync("s", "u", CancellationToken.None);

        Assert.True(handler.Calls.Count >= 6);
        Assert.All(handler.Calls, c =>
        {
            Assert.Equal("secret", c.Headers["X-Api-Key"]);
            Assert.Equal("pelly", c.Headers["X-Team"]);
        });
    }

    [Fact]
    public async Task ChatPromptTokenCount_PostsChatWithThinkTrue()
    {
        var handler = new StubHttpHandler().OnJson("/api/chat", """{"message":{"content":"x"},"done":true,"prompt_eval_count":50}""");
        var (backend, _) = Create(handler);

        var count = await backend.ChatPromptTokenCountAsync("SYS", "USER", CancellationToken.None);

        Assert.Equal(50, count);
        using var doc = JsonDocument.Parse(Assert.Single(handler.Calls).Body);
        var root = doc.RootElement;
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("think").GetBoolean());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(1, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("SYS", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("USER", messages[1].GetProperty("content").GetString());
    }
}
