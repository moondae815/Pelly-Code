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
            RedirectStandardInput = true, // closed right after start: a child that reads stdin sees EOF, not our console
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

        try { process.StandardInput.Close(); }
        catch (IOException) { /* the child already exited and closed its end */ }

        // Read both streams concurrently to avoid pipe-buffer deadlocks. Buffers are lock-guarded so a
        // snapshot can be taken even if a reader is abandoned (a descendant may keep the pipe open).
        var outBuf = new LockedBuffer();
        var errBuf = new LockedBuffer();
        var outTask = PumpAsync(process.StandardOutput.BaseStream, outBuf);
        var errTask = PumpAsync(process.StandardError.BaseStream, errBuf);

        using var timeoutCts = new CancellationTokenSource(spec.Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
            // The process is gone; wait for EOF only briefly, since an orphaned descendant can hold the pipes open.
            try
            {
                await Task.WhenAll(outTask, errTask).WaitAsync(DrainGrace, time, linked.Token);
            }
            catch (TimeoutException) { /* abandon readers; return what was read so far */ }
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await DrainAsync(process, outTask, errTask);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            timedOut = true;
        }

        var bytes = outBuf.Snapshot();
        return new ProcessResult(
            timedOut ? -1 : process.ExitCode,
            Encoding.UTF8.GetString(bytes),
            Encoding.UTF8.GetString(errBuf.Snapshot()),
            bytes,
            time.GetElapsedTime(started),
            timedOut);
    }

    private static Task PumpAsync(Stream stream, LockedBuffer sink) => Task.Run(async () =>
    {
        var chunk = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(chunk)) > 0) sink.Append(chunk, n);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    });

    private sealed class LockedBuffer
    {
        private readonly object _gate = new();
        private readonly MemoryStream _ms = new();
        public void Append(byte[] data, int count) { lock (_gate) _ms.Write(data, 0, count); }
        public byte[] Snapshot() { lock (_gate) return _ms.ToArray(); }
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
