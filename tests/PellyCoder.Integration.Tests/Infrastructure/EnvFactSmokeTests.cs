namespace PellyCoder.Integration.Tests.Infrastructure;

/// <summary>CI의 Env 필터와 Skip 동작을 확인하기 위한 스모크 테스트.</summary>
public class EnvFactSmokeTests
{
    [WindowsFact] public void Windows_Smoke() => Assert.True(TestEnv.IsWindows);
    [SqlServerFact] public void SqlServer_Smoke() => Assert.NotNull(TestEnv.SqlConnection);
    [OllamaFact] public void Ollama_Smoke() => Assert.NotNull(TestEnv.OllamaEndpoint);
}
