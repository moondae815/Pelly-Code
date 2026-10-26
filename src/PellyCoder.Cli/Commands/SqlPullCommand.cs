using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class SqlPullSettings : CommandSettings
{
    [CommandArgument(0, "<OBJECTS>")]
    [Description("가져올 객체 이름 (예: dbo.usp_X)")]
    public string[] Objects { get; init; } = [];

    [CommandOption("--from <SOURCE>")]
    [Description("가져올 서버 (운영 연결)")]
    public string? From { get; init; }

    [CommandOption("--out <DIR>")]
    [Description("저장할 폴더")]
    public string? Out { get; init; }

    [CommandOption("--force")]
    [Description("이미 있는 파일을 덮어씀")]
    public bool Force { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("작업 공간 설정 파일 경로 (기본: <루트>/pelly.json)")]
    public string? Config { get; init; }

    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }
}

/// <summary>
/// <c>pelly sql pull</c>: writes SSMS-shaped ALTER scripts with a pull record into the workspace pull folder
/// (production by default, <c>--from dev</c> for the development database). Exit 4 unless every object was written.
/// </summary>
public sealed class SqlPullCommand(
    IFileSystem fs, OptionsResolver resolver, IAnsiConsole console, TimeProvider time, IProcessRunner processes,
    Func<string, ISqlDefinitionReader> readerFactory, CancellationToken cancellation) : AsyncCommand<SqlPullSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SqlPullSettings settings, CancellationToken _)
    {
        PullSource source;
        if (settings.From is null || settings.From.Equals("prod", StringComparison.OrdinalIgnoreCase)) source = PullSource.Prod;
        else if (settings.From.Equals("dev", StringComparison.OrdinalIgnoreCase)) source = PullSource.Dev;
        else return Fail($"--from은 prod 또는 dev여야 합니다: {settings.From}");

        var anchor = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "pelly-anchor");
        var loaded = resolver.Resolve(anchor, settings.Config, null, null, null);
        if (loaded.Options is not { } options)
        {
            foreach (var error in loaded.Errors) Fail(error);
            return ExitCodes.EnvError;
        }

        var root = WorkspaceLocator.Resolve(fs, anchor).Root;
        var outDir = fs.Path.GetFullPath(settings.Out ?? fs.Path.Combine(root, options.Sql.PullDirectory));
        if (!IsInside(root, outDir))
            return Fail($"받을 폴더가 작업 공간 밖입니다: {outDir}");

        var open = source == PullSource.Prod ? SqlReaders.Production(options.Sql, readerFactory) : SqlReaders.Development(options.Sql, readerFactory);
        if (open.Reader is null || open.Endpoint is null)
        {
            if (open.Problem is not null) return Fail(open.Problem);
            return Fail("운영 연결이 설정되지 않았습니다. pelly.json의 sql.production(connectionString, allowedHosts, allowedDatabases)을 설정하거나, 비밀번호가 있는 연결은 환경 변수 PELLY_SQL_PROD_CONNECTION으로 지정하세요. 개발 DB에서 받으려면 --from dev를 쓰세요");
        }

        ISqlDefinitionReader? compareWith = null;
        if (source == PullSource.Prod)
            compareWith = SqlReaders.Development(options.Sql, readerFactory).Reader;

        IReadOnlyList<PullItem> items;
        try
        {
            items = await new SqlPullService(fs, time, options.Sql.CompatLevel)
                .PullAsync(new SqlPullRequest(settings.Objects, source, outDir, settings.Force), open.Reader, open.Endpoint, compareWith, cancellation);
        }
        catch (SqlUnavailableException ex)
        {
            return Fail($"{(source == PullSource.Prod ? "운영" : "개발")} DB에 접속할 수 없습니다: {ex.Message}");
        }

        foreach (var item in items)
        {
            if (item.Status == PullItemStatus.Written)
            {
                console.MarkupLine($"[green]{Markup.Escape($"받음: {item.Path}")}[/]");
                foreach (var notice in item.Notices) console.MarkupLine($"[yellow]{Markup.Escape($"경고: {notice}")}[/]");
            }
            else
            {
                foreach (var notice in item.Notices) console.MarkupLine($"[red]{Markup.Escape($"실패: {notice}")}[/]");
            }
        }

        if (items.FirstOrDefault(i => i.Status == PullItemStatus.Written)?.Path is { } written)
            await NoticeIfNotIgnoredAsync(root, outDir, written);

        var failed = items.Count(i => i.Status != PullItemStatus.Written);
        console.MarkupLine(Markup.Escape($"{items.Count - failed}개 받음, {failed}개 실패"));
        return failed > 0 ? ExitCodes.EnvError : ExitCodes.Success;
    }

    /// <summary>Shows a notice when git reports the pull folder as not ignored (exit 1). Not a repo, no git, or any other result skips it.</summary>
    private async Task NoticeIfNotIgnoredAsync(string root, string outDir, string writtenFile)
    {
        try
        {
            var result = await processes.RunAsync(new ProcessSpec("git", ["check-ignore", "-q", writtenFile], root, TimeSpan.FromSeconds(10)), cancellation);
            if (result.ExitCode != 1 || result.TimedOut) return;
        }
        catch (ProcessStartException)
        {
            return;
        }
        var shown = fs.Path.GetRelativePath(root, outDir).Replace('\\', '/').TrimEnd('/') + "/";
        console.MarkupLine($"[yellow]{Markup.Escape($"알림: {shown}가 .gitignore에 없습니다 — 받은 파일이 커밋될 수 있습니다")}[/]");
    }

    private bool IsInside(string root, string path)
    {
        var rel = fs.Path.GetRelativePath(root, path);
        return rel != ".." && !rel.StartsWith(".." + fs.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !rel.StartsWith("../", StringComparison.Ordinal) && !fs.Path.IsPathRooted(rel);
    }

    private int Fail(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return ExitCodes.EnvError;
    }
}
