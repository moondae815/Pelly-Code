using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Specs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class SpecCheckSettings : CommandSettings
{
    [CommandArgument(0, "<SPEC>")]
    [Description("검사할 명세 파일")]
    public string Spec { get; init; } = "";

    [CommandOption("--config <PATH>")]
    [Description("작업 공간 설정 파일 경로 (기본: <루트>/pelly.json)")]
    public string? Config { get; init; }

    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }
}

/// <summary><c>pelly spec check</c>: static checks of a spec; exit 4 on any error, 0 with warnings only.</summary>
public sealed class SpecCheckCommand(IFileSystem fs, OptionsResolver resolver, IAnsiConsole console) : Command<SpecCheckSettings>
{
    public override int Execute(CommandContext context, SpecCheckSettings settings, CancellationToken cancellation)
    {
        var spec = fs.Path.GetFullPath(settings.Spec);
        if (!fs.File.Exists(spec)) return Fail($"명세 파일을 찾을 수 없습니다: {spec}");

        var loaded = resolver.Resolve(spec, settings.Config, null, null, null);
        if (loaded.Options is not { } options)
        {
            foreach (var error in loaded.Errors) console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return ExitCodes.EnvError;
        }

        var result = SpecChecker.Check(fs, spec, options.Sql.CompatLevel);
        foreach (var issue in result.Issues)
        {
            var where = issue.Line > 0 ? $"줄 {issue.Line}: " : "";
            var (color, label) = issue.Severity == SpecIssueSeverity.Error ? ("red", "오류") : ("yellow", "경고");
            console.MarkupLine($"[{color}]{Markup.Escape($"{label} {where}[{issue.Code}] {issue.Message}")}[/]");
        }

        var errors = result.Issues.Count(i => i.Severity == SpecIssueSeverity.Error);
        var warnings = result.Issues.Count - errors;
        console.MarkupLine(Markup.Escape($"작업 {result.Spec.Tasks.Count}개, 오류 {errors}건, 경고 {warnings}건"));
        return errors > 0 ? ExitCodes.EnvError : ExitCodes.Success;
    }

    private int Fail(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return ExitCodes.EnvError;
    }
}
