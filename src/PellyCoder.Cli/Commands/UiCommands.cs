using System.IO.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Ui.Session;
using PellyCoder.Ui.Views;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

/// <summary>Shared parts of <c>pelly ui cs|sql</c>: GUI-only argument checks, the window's labels and the run function.</summary>
public static class UiCommands
{
    /// <summary>A run of the request typed in the window, through a runner that asks the window's questions.</summary>
    public static RunFunc RunFunc(RunnerFactory factory, PreparedRun run, Func<string, RunRequest> toRequest) =>
        (requestText, ui, ct) => factory(ui).RunAsync(toRequest(requestText), run.Options, ct);

    /// <summary>Long names (without <c>--</c>) of the options <see cref="GuiOptionErrors"/> rejects; shell completion leaves them out for <c>ui</c>.</summary>
    public static readonly IReadOnlyList<string> GuiRejectedOptions = ["yes", "pause-on-exit"];

    /// <summary>Errors for options that make no sense with a window, in the order they are checked.</summary>
    public static IEnumerable<string> GuiOptionErrors(RunSettings s)
    {
        if (s.Yes) yield return "`pelly ui`에서는 --yes를 쓸 수 없습니다 (창에서 승인합니다)";
        if (s.PauseOnExit) yield return "`pelly ui`에서는 --pause-on-exit가 필요 없습니다 (창이 남습니다)";
    }

    /// <summary>
    /// Prints every error in red, then shows the window: without errors it can run <paramref name="run"/>; with errors it
    /// starts Invalid and never runs. Returns the window's exit code (4 when there were errors).
    /// </summary>
    internal static int Launch(
        IUiLauncher launcher, IAnsiConsole console, CancellationToken shutdown,
        UiLaunch launch, IReadOnlyList<string> errors, Func<RunFunc> run)
    {
        foreach (var error in errors) console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
        RunFunc runFunc = errors.Count == 0 ? run() : (_, _, _) => Task.FromResult(ExitCodes.EnvError);
        return launcher.Run(launch, ui => new UiSession(runFunc, ui, errors, shutdown));
    }

    /// <summary>"Pelly — A.cs" and a target such as "A.cs 12번 줄" or "A.cs · C.M"; the file need not exist.</summary>
    internal static UiLaunch Describe(IFileSystem fs, RunSettings s, string path, string? request, int? line, string? member)
    {
        var name = fs.Path.GetFileName(path);
        var target = line is { } n ? $"{name} {n}번 줄" : member is null ? name : $"{name} · {member}";
        return new UiLaunch($"Pelly — {name}", target, request, s.DryRun);
    }
}

/// <summary><c>pelly ui cs</c>: the <c>cs</c> run in the approval window.</summary>
public sealed class UiCsCommand(
    IFileSystem fs, OptionsResolver resolver, RunnerFactory factory, IUiLauncher launcher, IAnsiConsole console, CancellationToken shutdown)
    : Command<CsSettings>
{
    public override int Execute(CommandContext context, CsSettings settings, CancellationToken _)
    {
        var (prepared, prepareErrors) = RunArguments.PrepareCs(settings, fs, resolver);
        List<string> errors = [.. RunArguments.ValidateCs(settings), .. UiCommands.GuiOptionErrors(settings), .. prepareErrors];
        var member = settings.Method is null ? null : settings.Class is null ? settings.Method : $"{settings.Class}.{settings.Method}";
        var launch = UiCommands.Describe(fs, settings, settings.File, settings.Request, settings.Line, member);
        return UiCommands.Launch(launcher, console, shutdown, launch, errors, () => UiCommands.RunFunc(
            factory, prepared!, text => RunArguments.CsRequest(settings, prepared!.File, text)));
    }
}

/// <summary><c>pelly ui sql</c>: the <c>sql</c> run in the approval window.</summary>
public sealed class UiSqlCommand(
    IFileSystem fs, OptionsResolver resolver, RunnerFactory factory, IUiLauncher launcher, IAnsiConsole console, CancellationToken shutdown)
    : Command<SqlSettings>
{
    public override int Execute(CommandContext context, SqlSettings settings, CancellationToken _)
    {
        var (prepared, prepareErrors) = RunArguments.PrepareSql(settings, fs, resolver);
        List<string> errors = [.. RunArguments.ValidateSql(settings), .. UiCommands.GuiOptionErrors(settings), .. prepareErrors];
        var launch = UiCommands.Describe(fs, settings, settings.File, settings.Request, settings.Line, settings.Object);
        return UiCommands.Launch(launcher, console, shutdown, launch, errors, () => UiCommands.RunFunc(
            factory, prepared!, text => RunArguments.SqlRequest(settings, prepared!.File, text)));
    }
}
