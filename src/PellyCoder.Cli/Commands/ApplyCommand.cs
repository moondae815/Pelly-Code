using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Specs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class ApplySettings : RunSettings
{
    [CommandArgument(0, "<SPEC>")]
    [Description("명세 파일 (changes/<id>.md)")]
    public string Spec { get; init; } = "";

    [CommandOption("--continue")]
    [Description("실패한 작업을 건너뛰고 나머지를 계속 실행")]
    public bool Continue { get; init; }

    [CommandOption("--resume")]
    [Description("이미 완료로 표시된 작업을 건너뛰고 이어서 실행")]
    public bool Resume { get; init; }

    [CommandOption("--review-at-end")]
    [Description("작업별 검토 대신 전부 끝낸 뒤 한 번에 검토")]
    public bool ReviewAtEnd { get; init; }
}

/// <summary><c>pelly apply</c>: runs the tasks of a change spec in order through <see cref="SpecRunner"/>.</summary>
/// <param name="impact">The impact search at the start of the apply; null (not registered) uses <see cref="ImpactFinders.Default"/>.</param>
public sealed class ApplyCommand(
    IFileSystem fs, OptionsResolver resolver, IRunner runner, IAnsiConsole console, IUserInteraction ui, TimeProvider time,
    IProcessRunner processes, CancellationToken cancellation, IImpactFinder? impact = null)
    : AsyncCommand<ApplySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ApplySettings settings, CancellationToken _)
    {
        if (settings.MaxRetries is < 0) return Fail("--max-retries는 0 이상이어야 합니다");

        var spec = fs.Path.GetFullPath(settings.Spec);
        if (!fs.File.Exists(spec)) return Fail($"명세 파일을 찾을 수 없습니다: {spec}");

        // The options file is looked up from the spec file: one apply uses one workspace.
        var loaded = resolver.Resolve(spec, settings.Config, settings.Think, settings.Model, settings.Endpoint);
        if (loaded.Options is not { } options)
        {
            foreach (var error in loaded.Errors) console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return ExitCodes.EnvError;
        }

        var specRunner = SpecRunnerFactory.Create(
            fs, options, ui, time, processes, (request, ct) => runner.RunDetailedAsync(request, options, ct), impact ?? ImpactFinders.Default(fs));
        var apply = new ApplyOptions(
            settings.Continue, settings.Resume, settings.ReviewAtEnd, settings.Yes, settings.DryRun, settings.MaxRetries, settings.Verbose);
        try
        {
            return await specRunner.RunAsync(spec, apply, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            console.MarkupLine("[yellow]사용자가 실행을 중단했습니다.[/]");
            return ExitCodes.UserCancelled;
        }
    }

    private int Fail(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return ExitCodes.EnvError;
    }
}
