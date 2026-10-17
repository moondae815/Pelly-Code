using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Processes;

/// <summary>
/// <see cref="IProcessRunner"/> backed by <see cref="Process"/>. The only place in Core allowed to use <see cref="Process"/>.
/// Process-tree kill on Windows: Windows 미확인 on a real machine (exercised by CI windows-latest).
/// </summary>
public sealed class SystemProcessRunner(TimeProvider time) : IProcessRunner
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        if (spec.Environment is not null)
            foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        var started = time.GetTimestamp();
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            throw new ProcessStartException(spec.FileName, ex);
        }

        // Read both streams concurrently to avoid pipe-buffer deadlocks.
        var outBuffer = new MemoryStream();
        var outTask = Task.Run(() => process.StandardOutput.BaseStream.CopyToAsync(outBuffer, CancellationToken.None));
        var errTask = Task.Run(() => process.StandardError.ReadToEndAsync(CancellationToken.None));

        using var timeoutCts = new CancellationTokenSource(spec.Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await DrainAsync(process, outTask, errTask);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            timedOut = true;
        }

        if (!timedOut)
        {
            await outTask;
            await errTask;
        }

        var bytes = outBuffer.ToArray();
        var stdErr = errTask.IsCompletedSuccessfully ? errTask.Result : "";
        return new ProcessResult(
            timedOut ? -1 : process.ExitCode,
            Encoding.UTF8.GetString(bytes),
            stdErr,
            bytes,
            time.GetElapsedTime(started),
            timedOut);
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
        catch (Win32Exception) { /* access denied / racing exit */ }
    }

    /// <summary>After a kill, wait briefly for exit and for the readers (an orphan could still hold a pipe open).</summary>
    private static async Task DrainAsync(Process process, Task outTask, Task errTask)
    {
        try
        {
            using var grace = new CancellationTokenSource(DrainGrace);
            await process.WaitForExitAsync(grace.Token);
            await Task.WhenAll(outTask, errTask).WaitAsync(grace.Token);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }
}
