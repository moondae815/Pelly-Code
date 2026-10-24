using System.ComponentModel;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class SpecNewSettings : CommandSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("명세 ID (소문자·숫자·하이픈, 예: add-refund)")]
    public string Id { get; init; } = "";

    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }
}

/// <summary><c>pelly spec new</c>: writes an empty <c>changes/&lt;id&gt;.md</c> template under the workspace root.</summary>
public sealed partial class SpecNewCommand(IFileSystem fs, IAnsiConsole console) : Command<SpecNewSettings>
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public override int Execute(CommandContext context, SpecNewSettings settings, CancellationToken cancellation)
    {
        if (!IdPattern().IsMatch(settings.Id))
            return Fail("ID는 소문자·숫자·하이픈이며 영문자나 숫자로 시작하는 64자 이하여야 합니다.");

        // The workspace is the current directory's; the placeholder file name is never read.
        var ws = WorkspaceLocator.Resolve(fs, fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "pelly-spec"));
        var dir = fs.Path.Combine(ws.Root, "changes");
        var path = fs.Path.Combine(dir, settings.Id + ".md");
        if (fs.File.Exists(path)) return Fail($"이미 있는 명세 파일입니다: {path}");

        fs.Directory.CreateDirectory(dir);
        fs.File.WriteAllText(path, Template(settings.Id), new System.Text.UTF8Encoding(false));
        console.MarkupLine(Markup.Escape($"명세 파일을 만들었습니다: {path}"));
        return ExitCodes.Success;
    }

    private static string Template(string id) =>
        $"""
        ---
        id: {id}
        status: draft
        ---
        # 제목

        ## 수용 기준
        - AC1: 이 변경이 끝났을 때 참이어야 하는 것

        ## 작업
        <!-- - [ ] <ID> <cs|sql> <파일>[#심볼] <요청> [(AC 참조)] -->

        """.Replace("\r\n", "\n");

    private int Fail(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return ExitCodes.EnvError;
    }
}
