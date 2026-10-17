namespace PellyCoder.Integration.Tests.Infrastructure;

public class TestEnvTests
{
    [Fact]
    public void SqlServer_IsUnavailable_WhenVariableEmpty()
        => Assert.False(TestEnv.IsAvailable(TestEnvKind.SqlServer, _ => null));

    [Fact]
    public void Ollama_RequiresBothEndpointAndModel()
        => Assert.False(TestEnv.IsAvailable(TestEnvKind.Ollama,
            name => name == "PELLY_TEST_OLLAMA" ? "http://localhost:11434" : null));

    [Fact]
    public void Ollama_IsAvailable_WhenBothSet()
        => Assert.True(TestEnv.IsAvailable(TestEnvKind.Ollama, _ => "x"));
}
