using System.Runtime.InteropServices;
using System.Text;
using PellyCoder.Cli;
using PellyCoder.Cli.Terminal;
using Spectre.Console;
using Spectre.Console.Cli;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
// Some terminals reject setting the input encoding, so each assignment is guarded on its own.
try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
try { Console.InputEncoding = Encoding.UTF8; } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }

using var shutdown = new ShutdownCoordinator();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C asks the run to stop (rollback, exit 2); a second one ends the process.
    if (shutdown.Token.IsCancellationRequested) return;
    e.Cancel = true;
    shutdown.RequestCancel();
};

// SIGTERM, SIGHUP and closing the console window (Windows CTRL_CLOSE maps to SIGHUP) must also roll back an
// unapproved patch: cancel, then hold the signal until the command has returned (after the orchestrator's rollback).
// Windows 미확인: console-close behavior is not verified on real Windows.
using var sigterm = RegisterTermination(PosixSignal.SIGTERM, shutdown);
using var sighup = RegisterTermination(PosixSignal.SIGHUP, shutdown);

var console = AnsiConsole.Console;
var app = new CommandApp(new DiTypeRegistrar(Composition.Services(new SpectreUserInteraction(console), console, shutdown.Token)));
app.Configure(CliApp.Configure);
try
{
    return CliApp.NormalizeExitCode(await app.RunAsync(args));
}
finally
{
    shutdown.SignalCompleted();
}

static PosixSignalRegistration? RegisterTermination(PosixSignal signal, ShutdownCoordinator shutdown)
{
    try
    {
        return PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            shutdown.RequestShutdownAndWait();
        });
    }
    catch (PlatformNotSupportedException)
    {
        return null;
    }
}
