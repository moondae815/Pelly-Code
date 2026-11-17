using Microsoft.Extensions.Logging;

namespace PellyCode.McpServer.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pelly-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void TwoProvidersOnOneFile_KeepEveryLine()
    {
        var path = Path.Combine(_dir, "server.log");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "earlier run\n");
        using (var a = new FileLoggerProvider(path))
        using (var b = new FileLoggerProvider(path))
        {
            // The startup-failure logger writes first, then the host's (another handle on the same file), then the first again.
            a.CreateLogger("A").LogError("first line");
            b.CreateLogger("B").LogInformation("second line");
            a.CreateLogger("A").LogError("third line");
        }

        var text = File.ReadAllText(path);
        Assert.StartsWith("earlier run\n", text);
        Assert.Contains("A: first line", text);
        Assert.Contains("B: second line", text);
        Assert.Contains("A: third line", text);
        Assert.True(text.IndexOf("first line", StringComparison.Ordinal) < text.IndexOf("second line", StringComparison.Ordinal));
        Assert.True(text.IndexOf("second line", StringComparison.Ordinal) < text.IndexOf("third line", StringComparison.Ordinal));
    }
}
