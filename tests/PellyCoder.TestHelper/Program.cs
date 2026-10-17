using System.Diagnostics;
using System.Text;

// echo <arg>: writes the first argument to stdout as UTF-8, unchanged.
// spawn: starts a long-lived child (a copy of this helper in "sleep" mode), prints its PID, then waits on it.
// flood: writes 1 MiB to stdout and 1 MiB to stderr.
// sleep: sleeps for two minutes.
switch (args.FirstOrDefault())
{
    case "echo":
        using (var stdout = Console.OpenStandardOutput())
        {
            var bytes = new UTF8Encoding(false).GetBytes(args[1]);
            stdout.Write(bytes, 0, bytes.Length);
        }
        return 0;
    case "spawn":
        using (var child = Process.Start(new ProcessStartInfo("dotnet",
            [Path.Combine(AppContext.BaseDirectory, "PellyCoder.TestHelper.dll"), "sleep"])
            { UseShellExecute = false, CreateNoWindow = true })!)
        {
            Console.Out.WriteLine(child.Id);
            Console.Out.Flush();
            child.WaitForExit();
        }
        return 0;
    case "flood":
        // 1 MiB on each stream, to prove neither pipe can block the other.
        var chunk = new string('x', 1 << 20);
        Console.Out.Write(chunk);
        Console.Out.Flush();
        Console.Error.Write(chunk);
        Console.Error.Flush();
        return 0;
    case "sleep":
        Thread.Sleep(TimeSpan.FromMinutes(2));
        return 0;
    default:
        return 2;
}
