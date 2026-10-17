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
        // Parent shell starts a long-lived child, prints the child's PID, then waits on it. The timeout must cover the
        // parent's start-up until the PID is printed: powershell + Start-Process took over 3 s on a loaded CI runner.
        var timeout = OperatingSystem.IsWindows() ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(1);
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("powershell",
                ["-NoProfile", "-Command", "$p = Start-Process -PassThru -WindowStyle Hidden ping -ArgumentList '-n','120','127.0.0.1'; Write-Output $p.Id; Wait-Process -Id $p.Id"],
                Path.GetTempPath(), timeout)
            : new ProcessSpec("/bin/sh", ["-c", "sleep 30 & echo $!; wait"], Path.GetTempPath(), timeout);

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
        // powershell -Command does not bind trailing tokens to $args, so Windows goes through a -File script.
        var script = Path.Combine(Path.GetTempPath(), $"pelly-echo-{Guid.NewGuid():N}.ps1");
        if (OperatingSystem.IsWindows())
            await File.WriteAllTextAsync(script, "[Console]::OutputEncoding=[Text.Encoding]::UTF8; [Console]::Out.Write($args[0])", new System.Text.UTF8Encoding(true), TestContext.Current.CancellationToken);
        try
        {
            var spec = OperatingSystem.IsWindows()
                ? new ProcessSpec("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, arg],
                    Path.GetTempPath(), TimeSpan.FromSeconds(30))
                : new ProcessSpec("/bin/sh", ["-c", "printf \"%s\" \"$1\"", "_", arg], Path.GetTempPath(), TimeSpan.FromSeconds(30));
            var r = await Runner.RunAsync(spec, CancellationToken.None);
            Assert.Equal(arg, r.StdOut.Trim());
        }
        finally { File.Delete(script); }
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
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("powershell",
                ["-NoProfile", "-Command", "$s='x'*1048576; [Console]::Out.Write($s); [Console]::Error.Write($s)"],
                Path.GetTempPath(), TimeSpan.FromSeconds(10))
            : new ProcessSpec("/bin/sh",
                ["-c", "head -c 1048576 /dev/zero | tr '\\0' x; head -c 1048576 /dev/zero | tr '\\0' y >&2"],
                Path.GetTempPath(), TimeSpan.FromSeconds(10));
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
