using System.Runtime.CompilerServices;
using Xunit.v3;

namespace PellyCoder.Integration.Tests.Infrastructure;

// 환경이 없으면 Skip되고, Trait "Env"=<kind>를 붙이는 Fact 특성들.

public sealed class WindowsFactAttribute : FactAttribute, ITraitAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Skip = EnvFact.SkipIfMissing(TestEnvKind.Windows);

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => EnvFact.Traits(TestEnvKind.Windows);
}

public sealed class SqlServerFactAttribute : FactAttribute, ITraitAttribute
{
    public SqlServerFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Skip = EnvFact.SkipIfMissing(TestEnvKind.SqlServer);

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => EnvFact.Traits(TestEnvKind.SqlServer);
}

public sealed class OllamaFactAttribute : FactAttribute, ITraitAttribute
{
    public OllamaFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Skip = EnvFact.SkipIfMissing(TestEnvKind.Ollama);

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => EnvFact.Traits(TestEnvKind.Ollama);
}

internal static class EnvFact
{
    public static string? SkipIfMissing(TestEnvKind kind) => TestEnv.IsAvailable(kind) ? null : TestEnv.SkipReason(kind);

    public static IReadOnlyCollection<KeyValuePair<string, string>> Traits(TestEnvKind kind) => [new("Env", kind.ToString())];
}
