using System.Diagnostics;
using System.Text.RegularExpressions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Processes;

public sealed class SystemProcessRunnerTests
{
    private static readonly SystemProcessRunner Runner = new(TimeProvider.System);

    private static ProcessSpec Shell(string unixScript, string windowsScript, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? env = null) =>
        OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd", ["/c", windowsScript], Path.GetTempPath(), timeout ?? TimeSpan.FromSeconds(30), env)
            : new ProcessSpec("/bin/sh", ["-c", unixScript], Path.GetTempPath(), timeout ?? TimeSpan.FromSeconds(30), env);

    /// <summary>
    /// The prebuilt test helper run through <c>dotnet</c>: identical on every OS and much quicker to start than powershell,
    /// which timed out on a loaded windows-latest runner.
    /// </summary>
    private static ProcessSpec Helper(TimeSpan timeout, params string[] args) =>
        new("dotnet", [Path.Combine(AppContext.BaseDirectory, "PellyCoder.TestHelper.dll"), .. args], Path.GetTempPath(), timeout);

    [Fact]
    public async Task RunsDotnetVersion_CapturesStdOut()
    {
        var r = await Runner.RunAsync(
            new ProcessSpec("dotnet", ["--version"], Path.GetTempPath(), TimeSpan.FromSeconds(60)), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.TimedOut);
        Assert.Matches(@"^\d+\.\d+", r.StdOut);
        Assert.NotEmpty(r.StdOutBytes);
    }

    [Fact]
    public async Task NonZeroExit_IsReported()
    {
        var r = await Runner.RunAsync(Shell("exit 3", "exit 3"), CancellationToken.None);
        Assert.Equal(3, r.ExitCode);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task Timeout_KillsProcessTree()
    {
        // The helper starts a long-lived child, prints its PID and waits on it. The timeout must cover the helper's start-up
        // (a dotnet host) until the PID is printed, so it stays generous on every OS; the kill is what ends the run.
        var timeout = TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 20 : 3);
        var spec = Helper(timeout, "spawn");

        var r = await Runner.RunAsync(spec, CancellationToken.None);

        Assert.True(r.TimedOut);
        Assert.Equal(-1, r.ExitCode);
        Assert.True(r.Duration < timeout + TimeSpan.FromSeconds(10), $"took {r.Duration}");
        var match = Regex.Match(r.StdOut, @"\d+");
        Assert.True(match.Success, $"the child PID was not printed before the timeout; stdout '{r.StdOut}', stderr '{r.StdErr}'");
        var pid = int.Parse(match.Value);
        Assert.True(await WaitUntilGoneAsync(pid, TimeSpan.FromSeconds(5)), $"child {pid} still alive");
    }

    [Fact]
    public async Task Cancellation_KillsAndThrows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner.RunAsync(Shell("sleep 30", "ping -n 40 127.0.0.1 >nul"), cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task ArgumentsWithSpacesAndKorean_RoundTrip()
    {
        const string arg = "공백 있는 인자 [x]";
        var r = await Runner.RunAsync(Helper(TimeSpan.FromSeconds(30), "echo", arg), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(arg, r.StdOut.Trim());
    }

    [Fact]
    public async Task EnvironmentVariables_ArePassed()
    {
        var r = await Runner.RunAsync(
            Shell("printf \"%s\" \"$PELLY_PROBE\"", "<nul set /p =%PELLY_PROBE%", env: new Dictionary<string, string> { ["PELLY_PROBE"] = "probe-값" }),
            CancellationToken.None);
        Assert.Contains("probe", r.StdOut);
    }

    [Fact]
    public async Task MissingExecutable_ThrowsProcessStartException()
    {
        var ex = await Assert.ThrowsAsync<ProcessStartException>(() => Runner.RunAsync(
            new ProcessSpec("definitely-not-a-real-exe-xyz", [], Path.GetTempPath(), TimeSpan.FromSeconds(5)), CancellationToken.None));
        Assert.Equal("definitely-not-a-real-exe-xyz", ex.FileName);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task LargeOutputOnBothStreams_DoesNotDeadlock()
    {
        var spec = Helper(TimeSpan.FromSeconds(30), "flood");
        var r = await Runner.RunAsync(spec, CancellationToken.None);
        Assert.False(r.TimedOut);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.StdOut.Length >= 1048576);
        Assert.True(r.StdErr.Length >= 1048576);
    }

    [Fact]
    public async Task OrphanedDescendantHoldingPipe_DoesNotHangAfterParentExit()
    {
        // Windows 미확인: no reliable inherited-handle equivalent, so this is Unix-only.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only (Windows 미확인)");
        var r = await Runner.RunAsync(
            new ProcessSpec("/bin/sh", ["-c", "sleep 30 & echo done"], Path.GetTempPath(), TimeSpan.FromSeconds(20)),
            CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.TimedOut);
        Assert.Contains("done", r.StdOut);
        Assert.True(r.Duration < TimeSpan.FromSeconds(10), $"took {r.Duration}");
    }

    [Fact]
    public async Task ScriptedRunner_WhenEmpty_ReturnsFallbackOrThrows()
    {
        var fake = new ScriptedProcessRunner();
        var spec = new ProcessSpec("x", [], ".", TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.RunAsync(spec, CancellationToken.None));
        fake.WhenEmpty(new ProcessResult(7, "", "", [], TimeSpan.Zero, false));
        var r = await fake.RunAsync(spec, CancellationToken.None);
        Assert.Equal(7, r.ExitCode);
        Assert.Equal(2, fake.Calls.Count);
    }

    private static async Task<bool> WaitUntilGoneAsync(int pid, TimeSpan window)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < window)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            await Task.Delay(100);
        }
        return false;
    }
}
