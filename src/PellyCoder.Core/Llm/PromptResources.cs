namespace PellyCoder.Core.Llm;

/// <summary>Embedded C# prompt texts. Bodies have the leading "# version: N" line and one trailing newline removed.</summary>
public sealed record PromptResources(string SystemCs, string FewShotCs, string Version)
{
    private const string Prefix = "PellyCoder.Core.Llm.Resources.";
    private const string VersionPrefix = "# version:";

    public static PromptResources LoadDefault()
    {
        var (system, systemVersion) = Load("system.cs.txt");
        var (fewShot, fewShotVersion) = Load("fewshot.cs.txt");
        if (systemVersion != fewShotVersion)
            throw new InvalidOperationException(
                $"Prompt resource versions differ: system.cs.txt={systemVersion}, fewshot.cs.txt={fewShotVersion}.");
        return new PromptResources(system, fewShot, "cs-v" + systemVersion);
    }

    private static (string Body, string Version) Load(string name)
    {
        using var stream = typeof(PromptResources).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {Prefix}{name}");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Replace("\r\n", "\n");
        var newline = text.IndexOf('\n');
        var first = newline < 0 ? text : text[..newline];
        if (!first.StartsWith(VersionPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name} must start with '{VersionPrefix} N'.");
        var version = first[VersionPrefix.Length..].Trim();
        var body = newline < 0 ? "" : text[(newline + 1)..];
        if (body.EndsWith('\n')) body = body[..^1];
        return (body, version);
    }
}
