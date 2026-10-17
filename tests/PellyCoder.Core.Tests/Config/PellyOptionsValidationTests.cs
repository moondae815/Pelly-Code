using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Config;

namespace PellyCoder.Core.Tests.Config;

public class PellyOptionsValidationTests
{
    private static OptionsLoadResult Load(string key, string value) =>
        PellyOptionsLoader.Load(new MockFileSystem(), null, null, new Dictionary<string, string?>(),
            new Dictionary<string, string?> { [key] = value });

    [Theory]
    [InlineData("llm:numCtx", "0")]
    [InlineData("llm:connectTimeoutSeconds", "0")]
    [InlineData("llm:firstTokenTimeoutSeconds", "0")]
    [InlineData("llm:streamIdleTimeoutSeconds", "-5")]
    [InlineData("log:retentionDays", "0")]
    [InlineData("retry:max", "-1")]
    [InlineData("build:timeoutSeconds", "0")]
    [InlineData("budget:think", "0")]
    [InlineData("budget:inputTotal", "0")]
    [InlineData("budget:codeOutput", "-1")]
    [InlineData("sampling:code:topK", "0")]
    [InlineData("sampling:think:topK", "0")]
    public void InvalidValue_IsError_NamingKey(string key, string value)
    {
        var r = Load(key, value);

        Assert.Null(r.Options);
        Assert.Contains(r.Errors, e => e.Contains(key, StringComparison.OrdinalIgnoreCase) && e.Contains(value));
    }

    [Fact]
    public void RetryMaxZero_IsValid()
    {
        var r = Load("retry:max", "0");

        Assert.Empty(r.Errors);
        Assert.Equal(0, r.Options!.Retry.Max);
    }
}
