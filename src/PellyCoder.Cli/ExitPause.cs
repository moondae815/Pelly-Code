using Spectre.Console;

namespace PellyCoder.Cli;

/// <summary>Keeps the console window open after a run (<c>--pause-on-exit</c>, for IDE External Tools windows).</summary>
public static class ExitPause
{
    public const string Flag = "--pause-on-exit";

    /// <summary>
    /// Decides from the raw arguments, so an argument error or an exception still pauses (R10).
    /// Arguments after <c>--</c> are ignored; redirected input never pauses (nobody could press a key).
    /// </summary>
    public static bool ShouldPause(IReadOnlyList<string> args, bool inputRedirected)
    {
        if (inputRedirected) return false;
        foreach (var arg in args)
        {
            if (arg == "--") return false;
            if (arg == Flag) return true;
        }
        return false;
    }

    /// <summary>Prints the exit code and waits for a key or a shutdown request (Ctrl+C, window close).</summary>
    public static void Wait(IAnsiConsole console, int exitCode, Func<bool> keyAvailable, Action readKey, CancellationToken shutdown)
    {
        console.MarkupLine($"[grey]종료 코드 {exitCode}. 아무 키나 누르면 창을 닫습니다…[/]");
        // Windows 미확인: the polling wait on Console.KeyAvailable/ReadKey is verified on macOS/Linux only.
        while (!shutdown.IsCancellationRequested)
        {
            bool available;
            try { available = keyAvailable(); }
            catch (InvalidOperationException) { return; } // no console attached: nothing to wait on
            if (available)
            {
                try { readKey(); }
                catch (InvalidOperationException) { }
                return;
            }
            shutdown.WaitHandle.WaitOne(50);
        }
    }
}
