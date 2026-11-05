using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

/// <summary>
/// <c>pelly edit</c>: every option of <c>pelly cs</c>, <c>sql</c> and <c>asp</c>; the file extension picks the command and
/// options that belong to another language are refused before anything runs.
/// </summary>
public sealed class EditSettings : RunSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("수정할 파일 (.cs → cs, .sql → sql, .asp·.inc → asp)")]
    public string File { get; init; } = "";

    [CommandArgument(1, "[REQUEST]")]
    [Description("수정 요청문 (생략하면 대화형으로 입력)")]
    public string? Request { get; init; }

    [CommandOption("--line <N>")]
    [Description("대상 줄 번호, IDE 커서 위치 (모든 언어)")]
    public int? Line { get; init; }

    [CommandOption("--class <NAME>")]
    [Description("C#: 대상 클래스 이름")]
    public string? Class { get; init; }

    [CommandOption("--method <NAME>")]
    [Description("C#: 대상 메서드 이름")]
    public string? Method { get; init; }

    [CommandOption("--test")]
    [Description("C#: 검증 때 테스트도 실행")]
    public bool Test { get; init; }

    [CommandOption("--verify-test <NAME>")]
    [Description("C#: 패치 전 실패·패치 뒤 통과해야 하는 테스트 (여러 번 지정 가능)")]
    public string[]? VerifyTest { get; init; }

    [CommandOption("--expect-break <NAME>")]
    [Description("C#: 이 변경으로 실패해도 되는 기존 테스트 (--verify-test와 함께)")]
    public string[]? ExpectBreak { get; init; }

    [CommandOption("--strict-baseline")]
    [Description("C#: 원래 있던 CS 오류도 종료 4로 처리")]
    public bool StrictBaseline { get; init; }

    [CommandOption("--warn-as-error")]
    [Description("C#: 빌드 경고를 오류로 취급")]
    public bool WarnAsError { get; init; }

    [CommandOption("--object <NAME>")]
    [Description("SQL: 대상 객체 이름, 예: dbo.MyProc")]
    public string? Object { get; init; }

    [CommandOption("--proc <NAME>")]
    [Description("ASP: 대상 Sub/Function 이름")]
    public string? Proc { get; init; }

    [CommandOption("--lines <A-B>")]
    [Description("ASP: 대상 줄 범위, 예: 10-25")]
    public string? Lines { get; init; }
}

public sealed class EditCommand(
    IFileSystem fs, OptionsResolver resolver, IRunner runner, IAnsiConsole console, IUserInteraction ui, CancellationToken cancellation)
    : AsyncCommand<EditSettings>
{
    private enum Language { CSharp, Sql, Asp }

    public override async Task<int> ExecuteAsync(CommandContext context, EditSettings settings, CancellationToken ct)
    {
        var language = Detect(settings);
        if (language is null)
            return ExitCodes.EnvError;

        var extension = fs.Path.GetExtension(settings.File).ToLowerInvariant();
        if (ForeignOption(settings, language.Value) is { } option)
            return Fail($"{option}은(는) {extension} 파일에 쓸 수 없습니다");

        console.MarkupLine($"[grey]{Markup.Escape(extension)} 파일 → pelly {Name(language.Value)}로 실행합니다[/]");
        return language.Value switch
        {
            Language.CSharp => await new CsCommand(fs, resolver, runner, console, ui, cancellation).ExecuteAsync(context, ToCs(settings), ct),
            Language.Sql => await new SqlCommand(fs, resolver, runner, console, ui, cancellation).ExecuteAsync(context, ToSql(settings), ct),
            _ => await new AspCommand(fs, resolver, runner, console, ui, cancellation).ExecuteAsync(context, ToAsp(settings), ct),
        };
    }

    /// <summary>.cs and .sql by name; anything else is ASP when <c>asp.extensions</c> (which needs the configuration) lists it.</summary>
    private Language? Detect(EditSettings s)
    {
        var extension = fs.Path.GetExtension(s.File);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return Language.CSharp;
        if (extension.Equals(".sql", StringComparison.OrdinalIgnoreCase)) return Language.Sql;

        var file = fs.Path.GetFullPath(s.File);
        if (!fs.File.Exists(file))
        {
            Fail($"파일을 찾을 수 없습니다: {file}");
            return null;
        }
        var loaded = resolver.Resolve(file, s.Config, s.Think, s.Model, s.Endpoint);
        if (loaded.Options is null)
        {
            foreach (var error in loaded.Errors) console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return null;
        }
        if (loaded.Options.Asp.Extensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            return Language.Asp;

        var shown = extension.Length == 0 ? "(확장자 없음)" : extension.ToLowerInvariant();
        Fail($"지원하지 않는 파일 형식입니다: {shown} (.cs → pelly cs, .sql → pelly sql, "
            + $"{string.Join("·", loaded.Options.Asp.Extensions)} → pelly asp)");
        return null;
    }

    /// <summary>The first option given that the detected language's command does not have, as written on the command line.</summary>
    private static string? ForeignOption(EditSettings s, Language language)
    {
        var csOnly = new (bool Given, string Name)[]
        {
            (s.Class is not null, "--class"), (s.Method is not null, "--method"), (s.Test, "--test"),
            (s.VerifyTest is { Length: > 0 }, "--verify-test"), (s.ExpectBreak is { Length: > 0 }, "--expect-break"),
            (s.StrictBaseline, "--strict-baseline"), (s.WarnAsError, "--warn-as-error"),
        };
        var sqlOnly = new (bool Given, string Name)[] { (s.Object is not null, "--object") };
        var aspOnly = new (bool Given, string Name)[] { (s.Proc is not null, "--proc"), (s.Lines is not null, "--lines") };

        var foreign = language switch
        {
            Language.CSharp => sqlOnly.Concat(aspOnly),
            Language.Sql => csOnly.Concat(aspOnly),
            _ => csOnly.Concat(sqlOnly),
        };
        return foreign.FirstOrDefault(o => o.Given).Name;
    }

    private static string Name(Language language) => language switch
    {
        Language.CSharp => "cs",
        Language.Sql => "sql",
        _ => "asp",
    };

    private static CsSettings ToCs(EditSettings s) => new()
    {
        File = s.File, Request = s.Request, Class = s.Class, Method = s.Method, Line = s.Line,
        StrictBaseline = s.StrictBaseline, WarnAsError = s.WarnAsError, Test = s.Test, VerifyTest = s.VerifyTest, ExpectBreak = s.ExpectBreak,
        Yes = s.Yes, DryRun = s.DryRun, Verbose = s.Verbose, MaxRetries = s.MaxRetries, Config = s.Config, Think = s.Think,
        Model = s.Model, Endpoint = s.Endpoint, PauseOnExit = s.PauseOnExit,
    };

    private static SqlSettings ToSql(EditSettings s) => new()
    {
        File = s.File, Request = s.Request, Object = s.Object, Line = s.Line,
        Yes = s.Yes, DryRun = s.DryRun, Verbose = s.Verbose, MaxRetries = s.MaxRetries, Config = s.Config, Think = s.Think,
        Model = s.Model, Endpoint = s.Endpoint, PauseOnExit = s.PauseOnExit,
    };

    private static AspSettings ToAsp(EditSettings s) => new()
    {
        File = s.File, Request = s.Request, Proc = s.Proc, Line = s.Line, Lines = s.Lines,
        Yes = s.Yes, DryRun = s.DryRun, Verbose = s.Verbose, MaxRetries = s.MaxRetries, Config = s.Config, Think = s.Think,
        Model = s.Model, Endpoint = s.Endpoint, PauseOnExit = s.PauseOnExit,
    };

    private int Fail(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return ExitCodes.EnvError;
    }
}
