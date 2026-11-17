using System.Text.RegularExpressions;

namespace PellyCode.McpServer.Tests;

/// <summary>
/// The shipped server guide (<c>deploy/mcp-server.md</c>) and the MCP part of the field checklist: every
/// <c>PellyCodeMcpServer.exe</c> command line uses only options the server reads, and the guide carries the facts the design fixes
/// (registration timeout, codemode caution, the AGENTS.md line word for word).
/// </summary>
public sealed class DocsTests
{
    // MCP server design §8, verbatim.
    private const string AgentsLine = "코드를 고친 뒤에는 끝내기 전에 반드시 `pelly_verify`를 부르고, 새 문제가 있으면 고친 뒤 다시 부른다. "
        + "메서드·SP·JS 함수의 이름이나 매개변수를 바꾸기 전에는 `pelly_impact`로 부르는 곳을 확인한다.";

    [Theory]
    [InlineData("deploy/field-checklist.md")]
    [InlineData("deploy/mcp-server.md")]
    public void ServerCommandLines_UseOnlyKnownOptions(string doc)
    {
        var lines = ServerLines(File.ReadAllText(RepoFile(doc)));

        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("--check", StringComparison.Ordinal));
        foreach (var line in lines)
            foreach (Match m in Regex.Matches(line, @"(?<=\s)--[a-z-]+"))
                Assert.True(CommandLine.Options.Contains(m.Value), $"{doc}: '{line}' uses {m.Value}, which the server does not read");
    }

    [Fact]
    public void Guide_CarriesRegistrationTimeoutCodemodeAndAgentsLine()
    {
        var text = File.ReadAllText(RepoFile("deploy/mcp-server.md"));

        Assert.Contains(AgentsLine, text, StringComparison.Ordinal);
        Assert.Contains("\"timeout\": 660000", text, StringComparison.Ordinal);
        Assert.Contains("\"codemode\": false", text, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"local\"", text, StringComparison.Ordinal);
        Assert.Contains("mcp-base", text, StringComparison.Ordinal);
        Assert.Contains("pelly test", text, StringComparison.Ordinal);
        Assert.Contains(PellyCode.McpServer.Verify.VerifyReport.NothingChecked, text, StringComparison.Ordinal);
        foreach (var bad in new[] { "/Users/", @"C:\Users\", @"C:\\Users\\", "/private/tmp" })
            Assert.DoesNotContain(bad, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guide_RegistrationExample_IsValidJson()
    {
        // The first ```json block is the opencode.json example people copy.
        var text = File.ReadAllText(RepoFile("deploy/mcp-server.md"));
        var block = Regex.Match(text, "```json\\r?\\n(.*?)```", RegexOptions.Singleline);
        Assert.True(block.Success, "no ```json block");
        using var doc = System.Text.Json.JsonDocument.Parse(block.Groups[1].Value);
        var pelly = doc.RootElement.GetProperty("mcp").GetProperty("pelly");
        Assert.Equal("local", pelly.GetProperty("type").GetString());
        Assert.Equal(660000, pelly.GetProperty("timeout").GetInt32());
        var command = pelly.GetProperty("command").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.EndsWith("PellyCodeMcpServer.exe", command[0], StringComparison.Ordinal);
        Assert.Contains("--root", command);
    }

    /// <summary>Lines in fenced blocks that run the server (with or without a folder in front of the exe name).</summary>
    private static List<string> ServerLines(string text)
    {
        var lines = new List<string>();
        foreach (Match block in Regex.Matches(text, @"```[a-z]*\r?\n(.*?)```", RegexOptions.Singleline))
            foreach (var raw in block.Groups[1].Value.Split('\n'))
            {
                var line = raw.Trim();
                if (Regex.IsMatch(line, @"\A(\S*[\\/])?PellyCodeMcpServer\.exe(\s|\z)")) lines.Add(line);
            }
        return lines;
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PellyCode.sln")))
                return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        throw new DirectoryNotFoundException("PellyCode.sln not found above the test binaries.");
    }
}
