using System.Text.Json;
using PellyCoder.Core;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public sealed class RecordCommandTests : IDisposable
{
    private const string Model = "qwen3.6:35b-a3b-coding-nvfp4";
    private static readonly string Digest = "cd2692a833e66c4c98991b67e9fbaa0bb15a93285baac9240c022f2f40075b6d";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pelly-record-" + Guid.NewGuid().ToString("N"));

    public RecordCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeBackend(LlmServerStatus status) : ILlmBackend
    {
        public IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<LlmServerStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(status);
    }

    private static LlmServerStatus Healthy() =>
        new(true, "0.40.0", true, false, null, TimeSpan.Zero, null, Digest, "safetensors", "nvfp4");

    private static PellyOptions Options()
    {
        var o = new PellyOptions();
        o.Llm.Model = Model;
        o.Llm.Endpoint = "http://ollama.internal.example:11434";
        return o;
    }

    private async Task<(int Code, string Out, string Path)> Record(LlmServerStatus status, params string[] extra)
    {
        var path = Path.Combine(_dir, "version-record.json");
        var parsed = EvalArguments.Parse(["record", "--out", path, .. extra]);
        Assert.Null(parsed.Error);
        var output = new StringWriter();
        var code = await RecordCommand.RunAsync(parsed, new FakeBackend(status), Options(), output, CancellationToken.None);
        return (code, output.ToString(), path);
    }

    [Fact]
    public async Task Record_WritesKeysInOrder_WithUtcTimestamp_AndNullGguf()
    {
        var (code, _, path) = await Record(Healthy());

        Assert.Equal(0, code);
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal(["pellyVersion", "ollamaVersion", "model", "modelDigest", "modelFormat", "modelQuantization", "modelGgufSha256", "recordedAt"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(PellyInfo.Version, root.GetProperty("pellyVersion").GetString());
        Assert.Equal("0.40.0", root.GetProperty("ollamaVersion").GetString());
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.Equal(Digest, root.GetProperty("modelDigest").GetString());
        Assert.Equal("safetensors", root.GetProperty("modelFormat").GetString());
        Assert.Equal("nvfp4", root.GetProperty("modelQuantization").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("modelGgufSha256").ValueKind);
        Assert.Matches(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z", root.GetProperty("recordedAt").GetString()!);
        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Record_GgufSha256_IsLowercased()
    {
        var upper = new string('A', 32) + new string('B', 32);

        var (code, _, path) = await Record(Healthy(), "--gguf-sha256", upper);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(upper.ToLowerInvariant(), doc.RootElement.GetProperty("modelGgufSha256").GetString());
    }

    [Fact]
    public async Task Record_BadGgufSha256_Exits4_WritesNothing()
    {
        var (code, output, path) = await Record(Healthy(), "--gguf-sha256", "not-hex");

        Assert.Equal(4, code);
        Assert.False(File.Exists(path));
        Assert.NotEmpty(output);
    }

    [Fact]
    public async Task Record_ModelNotOnServer_Exits3_WritesNothing()
    {
        var (code, _, path) = await Record(new LlmServerStatus(true, "0.40.0", false, false, null, TimeSpan.Zero, null));

        Assert.Equal(3, code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Record_ServerUnreachable_Exits4()
    {
        var (code, _, path) = await Record(new LlmServerStatus(false, null, false, false, null, TimeSpan.Zero, "refused"));

        Assert.Equal(4, code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Record_RequiresOut()
    {
        Assert.NotNull(EvalArguments.Parse(["record"]).Error);
        Assert.NotNull(EvalArguments.Parse(["record", "--out", "x", "--tasks", "a"]).Error);
        var ok = EvalArguments.Parse(["record", "--out", "x", "--gguf-sha256", "ab", "--config", "c.json"]);
        Assert.Null(ok.Error);
        Assert.Equal("ab", ok.GgufSha256);
    }
}
