using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Llm;

/// <summary>
/// Qwen BPE token counter backed by embedded vocab.json / merges.txt / tokenizer-meta.json.
/// Matches the Ollama server's prompt_eval_count (spike S4: 0% error). Loaded lazily once per process.
/// </summary>
public sealed class QwenTokenCounter : ITokenCounter
{
    private const string ResourcePrefix = "PellyCoder.Core.Llm.Resources.";
    private static readonly Lazy<QwenTokenCounter> SharedInstance = new(() => new QwenTokenCounter(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly BpeTokenizer _tokenizer;

    /// <summary>The model's control token strings (tokenizer-meta.json). Ollama raw mode reads these as real control tokens.</summary>
    public IReadOnlyCollection<string> SpecialTokens { get; }

    public static QwenTokenCounter Shared => SharedInstance.Value;

    private QwenTokenCounter()
    {
        var meta = JsonSerializer.Deserialize<Meta>(ReadAllText("tokenizer-meta.json"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("tokenizer-meta.json is empty.");

        SpecialTokens = meta.SpecialTokens.Keys.ToArray();

        Dictionary<string, int> vocab;
        using (var vocabStream = OpenResource("vocab.json"))
            vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabStream)
                ?? throw new InvalidOperationException("vocab.json is empty.");

        var merges = new List<string>();
        using (var reader = new StreamReader(OpenResource("merges.txt"), Encoding.UTF8))
            while (reader.ReadLine() is { } line)
                if (line.Length > 0)
                    merges.Add(line);

        var options = new BpeOptions(vocab)
        {
            Merges = merges,
            ByteLevel = true,
            SpecialTokens = meta.SpecialTokens,
            PreTokenizer = new RegexPreTokenizer(new Regex(meta.PreTokenizerRegex, RegexOptions.Compiled), meta.SpecialTokens),
            Normalizer = new NfcNormalizer(),
        };
        _tokenizer = BpeTokenizer.Create(options);
    }

    public int Count(string text) => text.Length == 0 ? 0 : _tokenizer.CountTokens(text);

    private static Stream OpenResource(string name) =>
        typeof(QwenTokenCounter).Assembly.GetManifestResourceStream(ResourcePrefix + name)
        ?? throw new InvalidOperationException($"Embedded resource not found: {ResourcePrefix}{name}");

    private static string ReadAllText(string name)
    {
        using var reader = new StreamReader(OpenResource(name), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed record Meta(string PreTokenizerRegex, Dictionary<string, int> SpecialTokens);

    private sealed class NfcNormalizer : Normalizer
    {
        public override string Normalize(string original) => original.Normalize(NormalizationForm.FormC);
        public override string Normalize(ReadOnlySpan<char> original) => original.ToString().Normalize(NormalizationForm.FormC);
    }
}
