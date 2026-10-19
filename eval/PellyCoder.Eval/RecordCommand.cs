using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using PellyCoder.Core;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;

namespace PellyCoder.Eval;

/// <summary>
/// <c>record --out &lt;file&gt; [--gguf-sha256 &lt;hex&gt;] [--config &lt;pelly.json&gt;]</c>: writes the Ollama version and the model's
/// identity (digest, format, quantization) read from the server the composition root connected to, so the field PC's model
/// can be compared with the one the baseline was measured on. The file never holds the server address.
/// Exit 0 written · 3 model not on the server, or the server reports no version / no 64-hex digest (nothing written) · 4 server unreachable,
/// model list unreadable, bad arguments or an unwritable file.
/// </summary>
public static partial class RecordCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(EvalArguments args, ILlmBackend backend, PellyOptions options, TextWriter output, CancellationToken ct)
    {
        if (args.Out is not { } path)
        {
            output.WriteLine("record에는 --out <파일>이 필요합니다");
            return 4;
        }

        string? gguf = null;
        if (args.GgufSha256 is { } given)
        {
            if (!Hex64().IsMatch(given))
            {
                output.WriteLine("--gguf-sha256은 64자리 16진수여야 합니다");
                return 4;
            }
            gguf = given.ToLowerInvariant();
        }

        var status = await backend.GetStatusAsync(ct);
        if (!status.Reachable)
        {
            output.WriteLine($"LLM 서버에 연결할 수 없습니다: {status.Error ?? "응답 없음"}");
            return 4;
        }
        if (!status.ModelInstalled)
        {
            // A failed /api/tags also reads as "not installed"; do not claim the model is missing then.
            if (status.Error is { } error)
            {
                output.WriteLine($"모델 목록을 읽지 못했습니다: {error}");
                return 4;
            }
            output.WriteLine($"모델이 서버에 없습니다: {options.Llm.Model}");
            return 3;
        }
        if (string.IsNullOrEmpty(status.Version) || status.ModelDigest is not { } digest || !Hex64().IsMatch(digest))
        {
            output.WriteLine("서버가 Ollama 버전 또는 64자리 16진수 모델 digest를 알리지 않았습니다 — 기록하지 않습니다");
            return 3;
        }

        // Anonymous object: property order is the key order of the file.
        var record = new
        {
            pellyVersion = PellyInfo.Version,
            ollamaVersion = status.Version,
            model = options.Llm.Model,
            modelDigest = digest.ToLowerInvariant(),
            modelFormat = status.ModelFormat,
            modelQuantization = status.ModelQuantization,
            modelGgufSha256 = gguf,
            recordedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        var full = Path.GetFullPath(path);
        var text = JsonSerializer.Serialize(record, Json);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, text + "\n", ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            output.WriteLine($"기록 파일을 쓸 수 없습니다 ({full}): {ex.Message}");
            return 4;
        }
        output.WriteLine($"기록했습니다: {full}");
        output.WriteLine(text);
        return 0;
    }

    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z")]
    private static partial Regex Hex64();
}
