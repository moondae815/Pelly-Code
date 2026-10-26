using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Eval;

/// <summary>
/// Makes a case's legacy <c>test.command</c> runnable outside a Visual Studio Developer prompt: a bare
/// <c>vstest.console.exe</c> that is not on PATH is replaced with the copy vswhere finds in the latest Visual Studio.
/// Anything else (an explicit path, another tool, no vswhere, nothing found) is returned unchanged, so the run fails the
/// same way it did before with the process-start message.
/// </summary>
public static class VstestCommand
{
    public const string BareName = "vstest.console.exe";

    private static readonly TimeSpan VswhereTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Resolves with the current process's PATH and <c>ProgramFiles(x86)</c>.</summary>
    public static Task<string> ResolveAsync(string command, IProcessRunner processes, IFileSystem fs, CancellationToken ct) =>
        ResolveAsync(command, processes, fs, Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"), ct);

    public static async Task<string> ResolveAsync(string command, IProcessRunner processes, IFileSystem fs, string? pathVariable,
        string? programFilesX86, CancellationToken ct)
    {
        var trimmed = command.TrimStart();
        var cut = trimmed.IndexOfAny([' ', '\t']);
        var head = cut < 0 ? trimmed : trimmed[..cut];
        if (!string.Equals(head, BareName, StringComparison.OrdinalIgnoreCase)) return command;
        if (OnPath(fs, pathVariable)) return command;

        var found = await FindWithVswhereAsync(processes, fs, programFilesX86, ct);
        return found is null ? command : $"\"{found}\"{(cut < 0 ? "" : trimmed[cut..])}";
    }

    private static bool OnPath(IFileSystem fs, string? pathVariable) =>
        (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(dir => fs.File.Exists(fs.Path.Combine(dir, BareName)));

    private static async Task<string?> FindWithVswhereAsync(IProcessRunner processes, IFileSystem fs, string? programFilesX86,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(programFilesX86)) return null;
        var vswhere = fs.Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!fs.File.Exists(vswhere)) return null;

        ProcessResult result;
        try
        {
            result = await processes.RunAsync(new ProcessSpec(vswhere,
                ["-latest", "-products", "*", "-find", @"**\TestPlatform\vstest.console.exe"],
                fs.Path.GetDirectoryName(vswhere) ?? ".", VswhereTimeout), ct);
        }
        catch (ProcessStartException)
        {
            return null;
        }

        if (result.ExitCode != 0 || result.TimedOut) return null;
        return result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(fs.File.Exists);
    }
}
