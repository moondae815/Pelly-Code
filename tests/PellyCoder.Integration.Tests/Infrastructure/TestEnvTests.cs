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

    [Fact]
    public void OllamaSql_Skips_WhenSqlMissing()
        => Assert.Equal("PELLY_TEST_SQL 환경 변수 필요",
            OllamaSqlFactAttribute.SkipReason(name => name == "PELLY_TEST_SQL" ? null : "x"));

    [Fact]
    public void OllamaSql_Skips_WhenModelMissing()
        => Assert.Equal("PELLY_TEST_OLLAMA, PELLY_TEST_MODEL 환경 변수 필요",
            OllamaSqlFactAttribute.SkipReason(name => name == "PELLY_TEST_MODEL" ? null : "x"));

    [Fact]
    public void OllamaSql_Runs_WhenBothSet()
        => Assert.Null(OllamaSqlFactAttribute.SkipReason(_ => "x"));
}
