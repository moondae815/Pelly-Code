using System.Diagnostics;
using ModelContextProtocol.Client;

namespace PellyCode.McpServer.Tests;

/// <summary>Starts the built server as a child process and checks it at the protocol level.</summary>
public sealed class ProtocolTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "pelly-mcp-" + Guid.NewGuid().ToString("N"));

    public ProtocolTests()
    {
        Directory.CreateDirectory(_repo);
        var p = Process.Start(new ProcessStartInfo("git", "init -q") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string ServerDll() => Path.Combine(AppContext.BaseDirectory, "PellyCodeMcpServer.dll");

    [Fact]
    public async Task ListTools_ReturnsExactlyTwo()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "pellycode-mcp-server",
            Command = "dotnet",
            Arguments = [ServerDll(), "--root", _repo],
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);

        var tools = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .Select(t => t.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["pelly_impact", "pelly_verify"], tools);
        Assert.Equal("pellycode-mcp-server", client.ServerInfo?.Name);
    }

    [Fact]
    public async Task Stdout_FirstBytesAreJsonRpc()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ServerDll());
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(_repo);
        using var proc = Process.Start(psi)!;
        _ = proc.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await proc.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""".AsMemory(), ct);
            await proc.StandardInput.FlushAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var first = await proc.StandardOutput.ReadLineAsync(cts.Token);

            Assert.NotNull(first);
            using var doc = System.Text.Json.JsonDocument.Parse(first); // the very first line must be a JSON object
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
            Assert.True(doc.RootElement.TryGetProperty("result", out _));
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task Verify_TakesNoArguments_ReportsNoChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "pellycode-mcp-server",
            Command = "dotnet",
            Arguments = [ServerDll(), "--root", _repo],
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        var verify = (await client.ListToolsAsync(cancellationToken: ct)).Single(t => t.Name == "pelly_verify");
        var schema = verify.JsonSchema;
        Assert.False(schema.TryGetProperty("properties", out var props) && props.EnumerateObject().Any(), schema.ToString());

        var result = await client.CallToolAsync("pelly_verify", new Dictionary<string, object?>(),
            new Progress<ModelContextProtocol.ProgressNotificationValue>(), cancellationToken: ct);
        var text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
        Assert.Equal("HEAD 이후 바뀐 파일이 없습니다", text);
    }

    [Fact]
    public async Task MalformedAppSettingsInWorkingFolder_ServerStillStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        // The user's project folder (the client's working folder) is not the server's content root.
        File.WriteAllText(Path.Combine(_repo, "appsettings.json"), "{ not json");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "pellycode-mcp-server",
            Command = "dotnet",
            Arguments = [ServerDll(), "--root", _repo],
            WorkingDirectory = _repo,
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        var tools = await client.ListToolsAsync(cancellationToken: ct);

        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public async Task LogFile_McpMode_KeepsStartupFailureAndHostLines()
    {
        var ct = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(_repo, "pelly.json"), "{ not json");
        var log = Path.Combine(_repo, "logs", "server.log");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "pellycode-mcp-server",
            Command = "dotnet",
            Arguments = [ServerDll(), "--root", _repo, "--log-file", log],
        });
        await using (var client = await McpClient.CreateAsync(transport, cancellationToken: ct))
        {
            await client.ListToolsAsync(cancellationToken: ct);
        }

        string text;
        using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
            text = await reader.ReadToEndAsync(ct);
        Assert.Contains("시작하지 못했습니다", text);
        Assert.Contains("Application started", text);
    }
}
