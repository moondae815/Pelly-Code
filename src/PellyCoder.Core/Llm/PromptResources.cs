using PellyCoder.Core.Models;

namespace PellyCoder.Core.Llm;

/// <summary>
/// Embedded C# prompt texts. Bodies have the leading "# version: N" line and one trailing newline removed.
/// <paramref name="Version"/> covers the edit prompts only; <paramref name="Locate"/> (target pick) is versioned on its own.
/// </summary>
public sealed record PromptResources(
    string SystemCs, string FewShotCs, string Version, string Locate,
    string SystemSql = "", string FewShotSql = "", string SqlVersion = "", string SystemCsGroup = "")
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
        var (systemGroup, systemGroupVersion) = Load("system.cs.group.txt");
        if (systemGroupVersion != systemVersion)
            throw new InvalidOperationException(
                $"Prompt resource versions differ: system.cs.txt={systemVersion}, system.cs.group.txt={systemGroupVersion}.");
        var (locate, _) = Load("locate.txt");
        var (systemSql, systemSqlVersion) = Load("system.sql.txt");
        var (fewShotSql, fewShotSqlVersion) = Load("fewshot.sql.txt");
        if (systemSqlVersion != fewShotSqlVersion)
            throw new InvalidOperationException(
                $"Prompt resource versions differ: system.sql.txt={systemSqlVersion}, fewshot.sql.txt={fewShotSqlVersion}.");
        return new PromptResources(system, fewShot, "cs-v" + systemVersion, locate, systemSql, fewShotSql, "sql-v" + systemSqlVersion, systemGroup);
    }

    /// <summary>Prompt version recorded in the run summary for the edit kind.</summary>
    public string VersionFor(EditKind kind) => kind switch
    {
        EditKind.Sql => SqlVersion,
        EditKind.CSharpTest => "cs-test-v1",
        _ => Version,
    };

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
