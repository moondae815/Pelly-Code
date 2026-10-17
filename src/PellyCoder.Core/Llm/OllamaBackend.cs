using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;

namespace PellyCoder.Core.Llm;

/// <summary>
/// Talks to Ollama's /api/generate in raw mode (design §8.2). Timeouts: <c>FirstTokenTimeoutSeconds</c> until the first
/// NDJSON line (one re-send), then <c>StreamIdleTimeoutSeconds</c> between lines (no re-send); status calls use
/// <c>ConnectTimeoutSeconds</c>. The caller owns <paramref name="http"/>.
/// </summary>
public sealed partial class OllamaBackend : ILlmBackend, IOllamaDiagnostics
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;
    private readonly TimeProvider _time;

    public OllamaBackend(HttpClient http, LlmOptions options, TimeProvider time)
    {
        _http = http;
        _options = options;
        _time = time;
        _http.BaseAddress ??= new Uri(options.Endpoint.TrimEnd('/') + "/");
    }

    public async IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildGenerateBody(request).ToJsonString();
        var firstTokenTimeout = TimeSpan.FromSeconds(_options.FirstTokenTimeoutSeconds);
        var idleTimeout = TimeSpan.FromSeconds(_options.StreamIdleTimeoutSeconds);

        for (var attempt = 1; ; attempt++)
        {
            var open = await OpenStreamAsync(body, firstTokenTimeout, ct);
            if (open is null)
            {
                if (attempt >= 2)
                    throw new LlmUnavailableException(
                        $"Ollama sent no data within {_options.FirstTokenTimeoutSeconds}s for model '{_options.Model}' (tried twice). Check the server and model load.");
                continue; // single re-send
            }

            using var response = open.Value.Response;
            using var reader = open.Value.Reader;
            var chunk = open.Value.First;
            while (true)
            {
                yield return chunk;
                if (chunk.Done)
                    yield break;

                var line = await ReadNonEmptyLineAsync(reader, idleTimeout, ct);
                if (line is null)
                    throw new LlmUnavailableException(
                        $"No data from Ollama for {_options.StreamIdleTimeoutSeconds}s mid-stream (model '{_options.Model}').");
                if (line.Length == 0)
                    throw new LlmUnavailableException("Ollama closed the stream before completing the response.");
                chunk = ParseChunk(line);
            }
        }
    }

    public async Task<LlmServerStatus> GetStatusAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? version;
        try
        {
            using var v = await SendJsonAsync(HttpMethod.Get, "api/version", null, ct);
            version = v.RootElement.TryGetProperty("version", out var ver) ? ver.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new LlmServerStatus(false, null, false, false, null, sw.Elapsed, Describe(ex));
        }

        var latency = sw.Elapsed;
        var installed = false;
        var loaded = false;
        string? renderer = null;
        var errors = new List<string>();

        try
        {
            using var tags = await SendJsonAsync(HttpMethod.Get, "api/tags", null, ct);
            installed = ListContainsModel(tags.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            errors.Add(Describe(ex));
        }

        try
        {
            using var ps = await SendJsonAsync(HttpMethod.Get, "api/ps", null, ct);
            loaded = ListContainsModel(ps.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            errors.Add(Describe(ex));
        }

        try
        {
            var showBody = new JsonObject { ["model"] = _options.Model }.ToJsonString();
            using var show = await SendJsonAsync(HttpMethod.Post, "api/show", showBody, ct);
            if (show.RootElement.TryGetProperty("modelfile", out var mf) && mf.ValueKind == JsonValueKind.String)
            {
                var m = RendererLine().Match(mf.GetString()!);
                renderer = m.Success ? m.Groups[1].Value : null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            errors.Add(Describe(ex));
        }

        return new LlmServerStatus(true, version, installed, loaded, renderer, latency,
            errors.Count == 0 ? null : string.Join("; ", errors));
    }

    public async Task<int?> ChatPromptTokenCountAsync(string system, string user, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["think"] = true,
            ["stream"] = false,
            ["keep_alive"] = _options.KeepAlive,
            ["options"] = new JsonObject { ["num_ctx"] = _options.NumCtx, ["num_predict"] = 1 },
        };
        using var doc = await SendJsonAsync(HttpMethod.Post, "api/chat", body.ToJsonString(), ct);
        return GetInt(doc.RootElement, "prompt_eval_count");
    }

    // ---------------------------------------------------------------- generate helpers

    private JsonObject BuildGenerateBody(LlmRequest r)
    {
        var stop = new JsonArray();
        foreach (var s in r.Stop)
            stop.Add(s);
        return new JsonObject
        {
            ["model"] = _options.Model,
            ["prompt"] = r.RawPrompt,
            ["raw"] = true,
            ["stream"] = true,
            ["keep_alive"] = r.KeepAlive,
            ["options"] = new JsonObject
            {
                ["num_ctx"] = r.NumCtx,
                ["num_predict"] = r.NumPredict,
                ["temperature"] = r.Sampling.Temperature,
                ["top_p"] = r.Sampling.TopP,
                ["top_k"] = r.Sampling.TopK,
                ["repeat_penalty"] = r.Sampling.RepeatPenalty,
                ["presence_penalty"] = r.Sampling.PresencePenalty,
                ["stop"] = stop,
            },
        };
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, string? json)
    {
        var req = new HttpRequestMessage(method, path);
        foreach (var (k, v) in _options.Headers)
            req.Headers.TryAddWithoutValidation(k, v);
        if (json is not null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>Sends the request and waits for the first non-empty line; null means the first-token timer expired.</summary>
    private async Task<(HttpResponseMessage Response, StreamReader Reader, LlmChunk First)?> OpenStreamAsync(
        string body, TimeSpan timeout, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        StreamReader? reader = null;
        try
        {
            using var timer = new CancellationTokenSource(timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
            try
            {
                using var req = NewRequest(HttpMethod.Post, "api/generate", body);
                response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                if (!response.IsSuccessStatusCode)
                    throw await HttpFailureAsync(response, linked.Token);

                reader = new StreamReader(await response.Content.ReadAsStreamAsync(linked.Token), Encoding.UTF8);
                string? line;
                do
                {
                    line = await reader.ReadLineAsync(linked.Token);
                } while (line is not null && line.Length == 0);

                if (line is null)
                    throw new LlmUnavailableException("Ollama closed the stream without sending any data.");
                var first = ParseChunk(line);
                var result = (response, reader, first);
                response = null;
                reader = null;
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timer.IsCancellationRequested)
            {
                return null;
            }
            catch (HttpRequestException ex)
            {
                throw new LlmUnavailableException($"Cannot reach Ollama at {_http.BaseAddress}: {ex.Message}", ex);
            }
        }
        finally
        {
            reader?.Dispose();
            response?.Dispose();
        }
    }

    /// <summary>Next non-empty line; null on idle timeout, empty string on end of stream.</summary>
    private async Task<string?> ReadNonEmptyLineAsync(StreamReader reader, TimeSpan idle, CancellationToken ct)
    {
        using var timer = new CancellationTokenSource(idle, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(linked.Token);
                if (line is null)
                    return "";
                if (line.Length > 0)
                    return line;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timer.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException ex)
        {
            throw new LlmUnavailableException($"Ollama stream failed: {ex.Message}", ex);
        }
    }

    private LlmChunk ParseChunk(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                throw new LlmUnavailableException($"Ollama reported an error for model '{_options.Model}': {err.GetString()}");

            var text = root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
            var done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            var durationNs = GetLong(root, "prompt_eval_duration");
            return new LlmChunk(
                text, done,
                GetInt(root, "prompt_eval_count"),
                GetInt(root, "prompt_eval_cached_count"),
                GetInt(root, "eval_count"),
                durationNs is null ? null : TimeSpan.FromTicks(durationNs.Value / 100),
                root.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String ? dr.GetString() : null);
        }
        catch (JsonException ex)
        {
            throw new LlmUnavailableException($"Ollama sent an unreadable line: {ex.Message}", ex);
        }
    }

    // ---------------------------------------------------------------- status helpers

    /// <summary>Whole-request call with the ConnectTimeoutSeconds timer; failures become LlmUnavailableException.</summary>
    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            using var req = NewRequest(method, path, json);
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
                throw await HttpFailureAsync(response, linked.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timer.IsCancellationRequested)
        {
            throw new LlmUnavailableException(
                $"Ollama did not answer {path} within {_options.ConnectTimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException($"Cannot reach Ollama at {_http.BaseAddress}: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new LlmUnavailableException($"Ollama sent an unreadable answer to {path}: {ex.Message}", ex);
        }
    }

    private bool ListContainsModel(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return false;
        var wanted = _options.Model;
        var alt = wanted.Contains(':') ? null : wanted + ":latest";
        foreach (var m in models.EnumerateArray())
            foreach (var field in new[] { "name", "model" })
                if (m.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (s == wanted || (alt is not null && s == alt))
                        return true;
                }

        return false;
    }

    private async Task<LlmUnavailableException> HttpFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? detail = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                detail = e.GetString();
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            // body is optional context only
        }

        return new LlmUnavailableException(
            $"Ollama returned {(int)response.StatusCode} for model '{_options.Model}': {detail ?? response.ReasonPhrase ?? "no details"}");
    }

    private static string Describe(Exception ex) => ex is LlmUnavailableException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";

    private static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    [GeneratedRegex(@"^RENDERER\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex RendererLine();
}
