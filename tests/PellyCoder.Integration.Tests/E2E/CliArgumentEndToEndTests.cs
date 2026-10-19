using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>The built pelly CLI rejects a value option without its value before Spectre parses (Program.cs wiring).</summary>
public sealed class CliArgumentEndToEndTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pelly-args-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ReportSanitizeOutWithoutValue_Exit4_WritesNothing()
    {
        var result = await new SystemProcessRunner(TimeProvider.System).RunAsync(
            new ProcessSpec("dotnet", [CliBinary.Dll(), "report", "--sanitize", "--out"], _dir, TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--out 옵션에 값이 없습니다", result.StdOut);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }
}
