using System.Runtime.InteropServices;

namespace PellyCoder.Integration.Tests.Infrastructure;

public enum TestEnvKind { Windows, SqlServer, Ollama }

/// <summary>통합 테스트가 필요로 하는 외부 환경의 존재 여부.</summary>
public static class TestEnv
{
    public const string SqlVariable = "PELLY_TEST_SQL";
    public const string OllamaVariable = "PELLY_TEST_OLLAMA";
    public const string ModelVariable = "PELLY_TEST_MODEL";

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static string? SqlConnection => Read(SqlVariable, Environment.GetEnvironmentVariable);
    public static string? OllamaEndpoint => Read(OllamaVariable, Environment.GetEnvironmentVariable);
    public static string? OllamaModel => Read(ModelVariable, Environment.GetEnvironmentVariable);

    public static bool IsAvailable(TestEnvKind kind) => IsAvailable(kind, Environment.GetEnvironmentVariable);

    public static bool IsAvailable(TestEnvKind kind, Func<string, string?> getEnv) => kind switch
    {
        TestEnvKind.Windows => IsWindows,
        TestEnvKind.SqlServer => Read(SqlVariable, getEnv) is not null,
        TestEnvKind.Ollama => Read(OllamaVariable, getEnv) is not null && Read(ModelVariable, getEnv) is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string SkipReason(TestEnvKind kind) => kind switch
    {
        TestEnvKind.Windows => "Windows에서만 실행",
        TestEnvKind.SqlServer => $"{SqlVariable} 환경 변수 필요",
        TestEnvKind.Ollama => $"{OllamaVariable}, {ModelVariable} 환경 변수 필요",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string? Read(string name, Func<string, string?> getEnv)
    {
        var value = getEnv(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
