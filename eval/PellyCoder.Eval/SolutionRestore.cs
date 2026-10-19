using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Eval;

/// <summary>
/// The restore contract shared by the runner and <c>validate --deep</c>. pelly builds with <c>--no-restore</c> and work copies
/// leave <c>obj/</c> out, so an SDK-style root must hold the solution to restore at its top: the first <c>*.sln</c> by
/// ordinal file name is restored, one time per task. <c>validate</c> warns when a root has none or several.
/// </summary>
public static class SolutionRestore
{
    /// <summary>Lines of the restore output kept in a skip detail or a warning.</summary>
    public const int TailLines = 5;

    private const int MaxLineLength = 300;

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>The <c>*.sln</c> files directly in <paramref name="root"/> (exact extension: not <c>.slnx</c>), ordinal by file name.</summary>
    public static IReadOnlyList<string> Solutions(IFileSystem fs, string root)
    {
        if (!fs.Directory.Exists(root)) return [];
        return [.. fs.Directory.GetFiles(root)
            .Where(f => string.Equals(fs.Path.GetExtension(f), ".sln", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => fs.Path.GetFileName(f), StringComparer.Ordinal)];
    }

    /// <summary>The solution the runner restores, or null.</summary>
    public static string? Pick(IFileSystem fs, string root) => Solutions(fs, root).FirstOrDefault();

    public static ProcessSpec Spec(string sln, string root) =>
        new("dotnet", ["restore", sln, "--nologo", "--disable-parallel"], root, Timeout);

    /// <summary>
    /// The last <see cref="TailLines"/> non-empty lines of the restore's stderr, or of its stdout when stderr is empty (MSBuild
    /// writes restore errors such as <c>NU1301</c> to stdout), trimmed, each cut to 300 characters, joined with <c> / </c>.
    /// </summary>
    public static string Tail(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(TailLines)
            .Select(l => l.Length > MaxLineLength ? l[..MaxLineLength] + "…" : l);
        return string.Join(" / ", lines);
    }

    /// <summary><c>종료 N</c>, or the timeout.</summary>
    public static string Head(ProcessResult result) => result.TimedOut ? $"시간 초과({Timeout.TotalMinutes:0}분)" : $"종료 {result.ExitCode}";

    /// <summary><paramref name="prefix"/> and the output tail after <c>: </c> (nothing when the restore printed nothing).</summary>
    public static string WithTail(string prefix, ProcessResult result) => Tail(result) is { Length: > 0 } tail ? $"{prefix}: {tail}" : prefix;
}
