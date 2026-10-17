using System.Text;
using PellyCoder.Cli;
using PellyCoder.Cli.Terminal;
using Spectre.Console;
using Spectre.Console.Cli;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
// Some terminals reject setting the input encoding, so each assignment is guarded on its own.
try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
try { Console.InputEncoding = Encoding.UTF8; } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C asks the run to stop (rollback, exit 2); a second one ends the process.
    if (cts.IsCancellationRequested) return;
    e.Cancel = true;
    cts.Cancel();
};

var console = AnsiConsole.Console;
var app = new CommandApp(new DiTypeRegistrar(Composition.Services(new SpectreUserInteraction(console), console, cts.Token)));
app.Configure(CliApp.Configure);
return CliApp.NormalizeExitCode(await app.RunAsync(args));
