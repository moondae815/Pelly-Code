using System.Runtime.CompilerServices;
using Xunit.v3;

namespace PellyCoder.Integration.Tests.Infrastructure;

/// <summary>
/// Real-model SQL tests: run only when both Ollama (<c>PELLY_TEST_OLLAMA</c>, <c>PELLY_TEST_MODEL</c>) and SQL Server
/// (<c>PELLY_TEST_SQL</c>) are configured. The trait is <c>Env=Ollama</c>, so CI (no model) filters them out.
/// </summary>
public sealed class OllamaSqlFactAttribute : FactAttribute, ITraitAttribute
{
    public OllamaSqlFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Skip = SkipReason(Environment.GetEnvironmentVariable);

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => EnvFact.Traits(TestEnvKind.Ollama);

    internal static string? SkipReason(Func<string, string?> getEnv)
    {
        var missing = new List<string>();
        if (!TestEnv.IsAvailable(TestEnvKind.Ollama, getEnv)) missing.Add(TestEnv.SkipReason(TestEnvKind.Ollama));
        if (!TestEnv.IsAvailable(TestEnvKind.SqlServer, getEnv)) missing.Add(TestEnv.SkipReason(TestEnvKind.SqlServer));
        return missing.Count == 0 ? null : string.Join(", ", missing);
    }
}
