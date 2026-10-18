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
            Assert.DoesNotContain("latencyMs", text);
            Assert.DoesNotContain("tokenizerHash", text);
            Assert.DoesNotContain("serverMajor", text);
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
}
