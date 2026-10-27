using System.Collections.Concurrent;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// The script from <c>pelly completion powershell</c>, loaded into real Windows PowerShell 5.1 and PowerShell 7, completes
/// through <c>TabExpansion2</c> as the design says (2026-10-27 §6.3). Each shell runs every case once; the tests read the results.
/// </summary>
public sealed class PowerShellCompletionTests
{
    private static readonly (string Name, string Line)[] Cases =
    [
        ("top-sp", "pelly sp"),
        ("top-com", "pelly com"),
        ("spec-sub", "pelly spec "),
        ("cs-ver", "pelly cs a.cs --ver"),
        ("doctor-off", "pelly doctor --off"),
        ("think", "pelly cs a.cs --think "),
        ("cs-path", @"pelly cs .\Ord"),
        ("sql-sub", "pelly sql p"),
        ("sql-path", @"pelly sql .\Orders"),
        ("config-path", @"pelly cs a.cs --config .\Ord"),
        ("exe-sp", "pelly.exe sp"),
        ("upper-sp", "pelly SP"),
        ("ui-yes", "pelly ui cs a.cs --y"),
        ("completion-sh", "pelly completion p"),
        ("sql-empty", "pelly sql "),
        ("sql-c", "pelly sql c"),
    ];

    // The driver prints "<case>\t<CompletionText>" per match. Everything is ASCII, so the console code page does not matter.
    private const string Driver = """
        param($ScriptPath, $CasesPath, $Bin, $Work)
        $env:PATH = $Bin + [System.IO.Path]::PathSeparator + $env:PATH
        . $ScriptPath
        Set-Location $Work
        foreach ($entry in Get-Content $CasesPath) {
            $parts = $entry.Split([char]'|', 2)
            $text = $parts[1]
            $result = TabExpansion2 -inputScript $text -cursorColumn $text.Length
            foreach ($match in $result.CompletionMatches) { "$($parts[0])`t$($match.CompletionText)" }
        }
        """;

    private static readonly ConcurrentDictionary<string, Task<ILookup<string, string>>> Results = new();

    private static Task<ILookup<string, string>> Complete(string shell) => Results.GetOrAdd(shell, RunShellAsync);

    private static async Task<ILookup<string, string>> RunShellAsync(string shell)
    {
        var runner = new SystemProcessRunner(TimeProvider.System);
        var root = Directory.CreateTempSubdirectory("pelly-ps-completion-").FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        // Native completers attach to a command PowerShell can resolve; these stand-ins are never run.
        File.WriteAllText(Path.Combine(bin, "pelly.cmd"), "@echo off\r\n");
        File.WriteAllBytes(Path.Combine(bin, "pelly.exe"), []);
        File.WriteAllText(Path.Combine(work, "OrderService.cs"), "class OrderService {}\n");
        File.WriteAllText(Path.Combine(work, "Orders.sql"), "SELECT 1\n");
        File.WriteAllText(Path.Combine(work, "customers.sql"), "SELECT 2\n");

        var generated = await runner.RunAsync(
            new ProcessSpec("dotnet", [CliBinary.Dll(), "completion", "powershell"], root, TimeSpan.FromMinutes(1)), CancellationToken.None);
        Assert.Equal(0, generated.ExitCode);
        var script = Path.Combine(root, "pelly-completion.ps1");
        File.WriteAllText(script, generated.StdOut);
        var driver = Path.Combine(root, "driver.ps1");
        File.WriteAllText(driver, Driver);
        var cases = Path.Combine(root, "cases.txt");
        File.WriteAllLines(cases, Cases.Select(c => c.Name + "|" + c.Line));

        var run = await runner.RunAsync(new ProcessSpec(shell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", driver, script, cases, bin, work],
            root, TimeSpan.FromMinutes(2)), CancellationToken.None);
        Assert.True(run.ExitCode == 0, $"{shell} exit {run.ExitCode}: {run.StdErr}{run.StdOut}");
        return run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2)
            .ToLookup(parts => parts[0], parts => parts[1]);
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task TopLevel_Completes(string shell)
    {
        var r = await Complete(shell);
        Assert.Contains("spec", r["top-sp"]);
        Assert.Contains("completion", r["top-com"]);
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task Branch_OffersSubcommands(string shell)
    {
        var r = await Complete(shell);
        Assert.Equal(["check", "draft", "new"], r["spec-sub"].Order().ToArray());
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task Options_Complete(string shell)
    {
        var r = await Complete(shell);
        Assert.Equal(["--verbose", "--verify-test"], r["cs-ver"].Order().ToArray());
        Assert.DoesNotContain("--offline-selftest", r["doctor-off"]);
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task Values_Complete(string shell)
    {
        var r = await Complete(shell);
        Assert.Equal(["always", "off", "retry"], r["think"].Order().ToArray());
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task PathCompletion_StillWorks(string shell)
    {
        var r = await Complete(shell);
        Assert.Contains(r["cs-path"], m => m.EndsWith("OrderService.cs", StringComparison.OrdinalIgnoreCase));
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task SqlBranch_OffersSubcommandsAndKeepsPaths(string shell)
    {
        var r = await Complete(shell);
        Assert.Contains("pull", r["sql-sub"]);
        Assert.Contains(r["sql-path"], m => m.EndsWith("Orders.sql", StringComparison.OrdinalIgnoreCase));
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task FreeValueOption_FallsBackToPaths(string shell)
    {
        var r = await Complete(shell);
        Assert.Contains(r["config-path"], m => m.EndsWith("OrderService.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r["config-path"], m => m.StartsWith("--", StringComparison.Ordinal));
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task ExeName_Completes(string shell)
    {
        var r = await Complete(shell);
        Assert.Contains("spec", r["exe-sp"]);
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task Prefix_IgnoresCase(string shell)
    {
        var r = await Complete(shell);
        Assert.Contains("spec", r["upper-sp"]);
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task Completion_OffersShellName(string shell)
    {
        var r = await Complete(shell);
        Assert.Equal(["powershell"], r["completion-sh"].ToArray());
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task SqlBranch_OffersFilesBesideSubcommands(string shell)
    {
        var r = await Complete(shell);
        Assert.Contains("pull", r["sql-empty"]);
        Assert.Contains(r["sql-empty"], m => m.EndsWith("Orders.sql", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("check", r["sql-c"]);
        Assert.Contains(r["sql-c"], m => m.EndsWith("customers.sql", StringComparison.OrdinalIgnoreCase));
    }

    [WindowsTheory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task UiEdit_HidesRejectedOptions(string shell)
    {
        var r = await Complete(shell);
        Assert.DoesNotContain("--yes", r["ui-yes"]);
    }
}
