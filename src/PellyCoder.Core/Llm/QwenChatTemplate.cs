using System.IO.Abstractions;
using System.Text.Json;

namespace PellyCoder.Core.Llm;

/// <summary>
/// Raw-mode ChatML template for Qwen (design D10, verified in spike S1). The assembled string is sent verbatim,
/// so <see cref="Build"/> adds nothing beyond the template fields.
/// </summary>
public sealed record QwenChatTemplate(
    string SystemStart, string SystemEnd, string UserStart, string UserEnd,
    string AssistantStart, string ThinkOpen, string ThinkClose, string EndOfTurn)
{
    private const string ResourceName = "PellyCoder.Core.Llm.Resources.template.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Prefill used when thinking is skipped: an empty think block.</summary>
    public string EmptyThink => ThinkOpen + ThinkClose;

    public static QwenChatTemplate LoadDefault()
    {
        using var stream = typeof(QwenChatTemplate).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), ResourceName);
    }

    public static QwenChatTemplate LoadFromFile(IFileSystem fs, string path) =>
        Parse(fs.File.ReadAllText(path), path);

    public string Build(string system, string user) =>
        SystemStart + system + SystemEnd + UserStart + user + UserEnd + AssistantStart;

    private static QwenChatTemplate Parse(string json, string source) =>
        JsonSerializer.Deserialize<QwenChatTemplate>(json, JsonOptions)
        ?? throw new InvalidOperationException($"Template is empty: {source}");
}
