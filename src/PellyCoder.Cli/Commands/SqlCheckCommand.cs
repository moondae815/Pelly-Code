using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class SqlCheckSettings : CommandSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("검사할 SQL 파일")]
    public string File { get; init; } = "";

    [CommandOption("--object <NAME>")]
    [Description("대상 객체 이름, 예: dbo.MyProc")]
    public string? Object { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("작업 공간 설정 파일 경로 (기본: <루트>/pelly.json)")]
    public string? Config { get; init; }

    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }
}

/// <summary>
/// <c>pelly sql check</c>: the check before running an edited file in SSMS against production. Never calls the model and never
/// changes the file. Exit 0 = safe or new, 1 = production changed since the pull, 3 = several objects, 4 = cannot confirm.
/// </summary>
public sealed class SqlCheckCommand(
    IFileSystem fs, OptionsResolver resolver, IAnsiConsole console,
    Func<string, ISqlDefinitionReader> readerFactory, CancellationToken cancellation) : AsyncCommand<SqlCheckSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SqlCheckSettings settings, CancellationToken _)
    {
        var anchor = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "pelly-anchor");
        var loaded = resolver.Resolve(anchor, settings.Config, null, null, null);
        if (loaded.Options is not { } options)
        {
            foreach (var error in loaded.Errors) Print("red", error);
            return ExitCodes.EnvError;
        }

        var path = fs.Path.GetFullPath(settings.File);
        string text;
        try
        {
            if (!fs.File.Exists(path)) return Fail($"파일을 찾을 수 없습니다: {settings.File}");
            text = TextFileCodec.Read(fs, path).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail($"파일을 읽을 수 없습니다: {settings.File} ({ex.Message})");
        }

        var script = SqlBatchSplitter.Split(text, options.Sql.CompatLevel);
        var resolution = SqlTargetResolver.Resolve(script, settings.Object);
        if (resolution.Target is not { ObjectName: { } name } target)
        {
            Print("red", resolution.Problem ?? "대상 객체를 정할 수 없습니다");
            return resolution.Ambiguous ? ExitCodes.LocateFailed : ExitCodes.EnvError;
        }

        var open = SqlReaders.Production(options.Sql, readerFactory);
        var check = await SqlProdComparer.CompareAsync(text, target, name, open, options.Sql.CompatLevel, cancellation);
        switch (check.Outcome)
        {
            case ProdCheckOutcome.SameAsProd:
                Print("green", "운영과 같음 (이미 반영됨 또는 아직 고치지 않음)");
                return ExitCodes.Success;
            case ProdCheckOutcome.UnchangedSincePull:
                Print("green", "받은 뒤 운영이 바뀌지 않음");
                return ExitCodes.Success;
            case ProdCheckOutcome.NotInProd:
                Print("green", check.Message);
                return ExitCodes.Success;
            case ProdCheckOutcome.ChangedSincePull:
                Print("red", $"받은 뒤 운영이 바뀜 — 반영하면 그 변경을 덮어씁니다. {check.Message}");
                return ExitCodes.VerifyFailed;
            case ProdCheckOutcome.DiffersNoRecord:
                Print("red", "받은 기록 없음 — 운영 변경 여부를 확인할 수 없습니다. 파일이 운영과 다릅니다");
                return ExitCodes.EnvError;
            case ProdCheckOutcome.NotConfigured:
                return Fail("운영 연결이 설정되지 않아 확인할 수 없습니다. pelly.json의 sql.production(connectionString, allowedHosts, allowedDatabases)을 설정하거나, 비밀번호가 있는 연결은 환경 변수 PELLY_SQL_PROD_CONNECTION으로 지정하세요");
            default:
                return Fail($"운영 DB를 확인할 수 없습니다: {check.Message}");
        }
    }

    private int Fail(string message)
    {
        Print("red", message);
        return ExitCodes.EnvError;
    }

    private void Print(string color, string message) => console.MarkupLine($"[{color}]{Markup.Escape(message)}[/]");
}
