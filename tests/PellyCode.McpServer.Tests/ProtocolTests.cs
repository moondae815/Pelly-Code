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
}
