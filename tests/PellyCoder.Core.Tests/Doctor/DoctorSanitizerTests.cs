using PellyCoder.Core.Doctor;

namespace PellyCoder.Core.Tests.Doctor;

public class DoctorSanitizerTests
{
    private static readonly string[] Secrets =
        ["C:\\Users\\kim", "SECRETHOST", "P@ssw0rd", "/home/kim", "evil-row", "db.corp.local"];

    [Fact]
    public void DoctorSanitize_InjectedDetail_NeverAppears()
    {
        var checks = new List<DoctorCheck>
        {
            new("Ollama 서버", DoctorStatus.Ok, "http://SECRETHOST:11434 C:\\Users\\kim", "P@ssw0rd 를 바꾸세요",
                new Dictionary<string, string> { ["ollamaVersion"] = "0.9.1", ["latencyMs"] = "12\n", ["extra"] = "db.corp.local" }),
            new("pelly", DoctorStatus.Ok, "/home/kim", null,
                new Dictionary<string, string> { ["pellyVersion"] = "1.0.0", ["tokenizerHash"] = "SECRETHOST/x", ["loaded"] = "true" }),
            new("SQL 연결", DoctorStatus.Fail, "Server=SECRETHOST;Password=P@ssw0rd", "db.corp.local",
                new Dictionary<string, string> { ["canViewDefinition"] = "false", ["serverMajor"] = "١٦" }),
            new("evil-row", DoctorStatus.Warn, "C:\\Users\\kim", null, new Dictionary<string, string> { ["found"] = "true" }),
        };

        foreach (var text in new[] { DoctorSanitizer.ToJson(checks), DoctorSanitizer.ToMarkdown(checks) })
        {
            foreach (var secret in Secrets) Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain("12\\n", text);
            Assert.DoesNotContain("١٦", text);
            Assert.Contains("0.9.1", text);
            Assert.Contains("기타", text);
        }
    }

    [Fact]
    public void UnknownRowName_IsOther()
    {
        var json = DoctorSanitizer.ToJson([new DoctorCheck("whatever", DoctorStatus.Warn, "d", null)]);
        Assert.Contains("\"기타\"", json);
        Assert.DoesNotContain("whatever", json);
    }

    [Fact]
    public void FactsOutsideWhitelist_Dropped()
    {
        var json = DoctorSanitizer.ToJson([new DoctorCheck("모델", DoctorStatus.Ok, "d", null,
            new Dictionary<string, string> { ["loaded"] = "true", ["path"] = "abc", ["ttftMs"] = "5" })]);
        Assert.Contains("loaded", json);
        Assert.DoesNotContain("path", json);
        Assert.DoesNotContain("ttftMs", json);
        Assert.DoesNotContain("Detail", json);
    }

    [Fact]
    public void VersionFacts_FollowVersionRule_AndEmptyIsOmitted()
    {
        var json = DoctorSanitizer.ToJson([
            new DoctorCheck(DoctorFacts.OllamaServer, DoctorStatus.Ok, "d", null,
                new Dictionary<string, string> { [DoctorFacts.OllamaVersion] = "v0.9", [DoctorFacts.LatencyMs] = "" }),
            new DoctorCheck(DoctorFacts.DotnetSdk, DoctorStatus.Ok, "d", null,
                new Dictionary<string, string> { [DoctorFacts.DotnetVersion] = "10.0.100" }),
            new DoctorCheck(DoctorFacts.Pelly, DoctorStatus.Ok, "d", null,
                new Dictionary<string, string> { [DoctorFacts.PellyVersion] = "" }),
        ]);
        Assert.Contains("\"invalid\"", json);
        Assert.DoesNotContain("v0.9", json);
        Assert.DoesNotContain(DoctorFacts.LatencyMs, json);
        Assert.DoesNotContain(DoctorFacts.PellyVersion, json);
        Assert.Contains("10.0.100", json);
    }

    [Theory]
    [InlineData("v4.8", "v4.8")]
    [InlineData("v4.5.2", "v4.5.2")]
    [InlineData("v4.8\n", "invalid")]
    [InlineData("4.8", "invalid")]
    [InlineData("vX", "invalid")]
    public void FrameworkVersion_UsesMonikerRule(string input, string expected)
    {
        var json = DoctorSanitizer.ToJson([new DoctorCheck(DoctorFacts.TargetingPack, DoctorStatus.Ok, "d", null,
            new Dictionary<string, string> { [DoctorFacts.FrameworkVersion] = input })]);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var value = doc.RootElement.GetProperty("checks")[0].GetProperty("Facts").GetProperty(DoctorFacts.FrameworkVersion).GetString();
        Assert.Equal(expected, value);
    }

    private static string? FactOf(string row, string key, string value)
    {
        var json = DoctorSanitizer.ToJson([new DoctorCheck(row, DoctorStatus.Ok, "d", null, new Dictionary<string, string> { [key] = value })]);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("checks")[0].GetProperty("Facts").TryGetProperty(key, out var v) ? v.GetString() : null;
    }

    [Theory]
    [InlineData(DoctorFacts.Model, DoctorFacts.Loaded, "true", "true")]
    [InlineData(DoctorFacts.Model, DoctorFacts.Loaded, "false", "false")]
    [InlineData(DoctorFacts.Model, DoctorFacts.Loaded, "True", "invalid")]
    [InlineData(DoctorFacts.Model, DoctorFacts.Loaded, "SECRETHOST", "invalid")]
    [InlineData(DoctorFacts.TokenizerMatch, DoctorFacts.Match, "yes", "invalid")]
    [InlineData(DoctorFacts.MsBuild, DoctorFacts.Found, "true\n", "invalid")]
    [InlineData(DoctorFacts.TargetingPack, DoctorFacts.Installed, "1", "invalid")]
    [InlineData(DoctorFacts.Sql, DoctorFacts.CanViewDefinition, "db.corp.local", "invalid")]
    [InlineData(DoctorFacts.Sql, DoctorFacts.ServerMajor, "16", "16")]
    [InlineData(DoctorFacts.Sql, DoctorFacts.ServerMajor, "١٦", "invalid")]
    [InlineData(DoctorFacts.Sql, DoctorFacts.ServerMajor, "1234567890", "invalid")]
    [InlineData(DoctorFacts.OllamaServer, DoctorFacts.LatencyMs, "12\n", "invalid")]
    [InlineData(DoctorFacts.OllamaServer, DoctorFacts.LatencyMs, "-3", "invalid")]
    [InlineData(DoctorFacts.OllamaServer, DoctorFacts.LatencyMs, "123456789", "123456789")]
    [InlineData(DoctorFacts.Latency, DoctorFacts.TtftMs, "4.5", "invalid")]
    [InlineData(DoctorFacts.Latency, DoctorFacts.TtftMs, "450", "450")]
    [InlineData(DoctorFacts.Pelly, DoctorFacts.TokenizerHash, "0123456789ab", "0123456789ab")]
    [InlineData(DoctorFacts.Pelly, DoctorFacts.TokenizerHash, "0123456789AB", "invalid")]
    [InlineData(DoctorFacts.Pelly, DoctorFacts.TokenizerHash, "0123456789abc", "invalid")]
    [InlineData(DoctorFacts.Pelly, DoctorFacts.TokenizerHash, "SECRETHOST/x", "invalid")]
    public void TypedFacts_AreCheckedPerKey(string row, string key, string value, string expected) =>
        Assert.Equal(expected, FactOf(row, key, value));
}
