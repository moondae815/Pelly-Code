using System.ComponentModel;
using System.Globalization;
using System.IO.Abstractions;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class DoctorSettings : CommandSettings
{
    [CommandOption("--config <PATH>")]
    [Description("작업 공간 설정 파일 경로 (기본: <루트>/pelly.json)")]
    public string? Config { get; init; }

    [CommandOption("--model <TAG>")]
    [Description("모델 태그 (llm.model 덮어쓰기)")]
    public string? Model { get; init; }

    [CommandOption("--endpoint <URL>")]
    [Description("Ollama 주소 (llm.endpoint 덮어쓰기)")]
    public string? Endpoint { get; init; }

    [CommandOption("--project <CSPROJ>")]
    [Description("타겟팅 팩을 확인할 프로젝트 파일")]
    public string? Project { get; init; }

    [CommandOption("--sanitize")]
    [Description("허용 목록 방식의 비식별 결과(json·md)를 파일로 저장")]
    public bool Sanitize { get; init; }

    [CommandOption("--out <DIR>")]
    [Description("--sanitize 출력 폴더(기본: 현재 폴더)")]
    public string? Out { get; init; }

    [CommandOption("--offline-selftest", IsHidden = true)]
    public bool OfflineSelfTest { get; init; }

    public override ValidationResult Validate() =>
        Out is not null && !Sanitize
            ? ValidationResult.Error("--out은 --sanitize와 함께 쓰세요.")
            : ValidationResult.Success();
}

/// <summary><c>pelly doctor</c>: prints the environment checks as a table; exit 4 when any check failed.</summary>
public sealed class DoctorCommand(
    IFileSystem fs, OptionsResolver resolver, IDoctorRunner doctor, IAnsiConsole console, CancellationToken cancellation)
    : AsyncCommand<DoctorSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings settings, CancellationToken _)
    {
        if (settings.OfflineSelfTest)
        {
            // Returns before options, the runner or any HTTP client are touched.
            var rows = Core.Doctor.OfflineSelfTest.Run();
            WriteTable(rows);
            return rows.Any(c => c.Status != DoctorStatus.Ok) ? ExitCodes.EnvError : ExitCodes.Success;
        }

        // The workspace is the current directory; the placeholder file name is never read.
        var anchor = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "pelly-doctor");
        if (settings.Project is { } project && !fs.File.Exists(project))
        {
            console.MarkupLine($"[red]프로젝트 파일을 찾을 수 없습니다: {Markup.Escape(project)}[/]");
            return ExitCodes.EnvError;
        }

        var loaded = resolver.Resolve(anchor, settings.Config, null, settings.Model, settings.Endpoint);
        if (loaded.Options is null)
        {
            foreach (var error in loaded.Errors) console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return ExitCodes.EnvError;
        }

        var doctorContext = new DoctorContext(
            resolver.UserConfigPath,
            resolver.WorkspaceConfigPath(anchor, settings.Config),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            settings.Project is null ? null : fs.Path.GetFullPath(settings.Project));

        IReadOnlyList<DoctorCheck> checks;
        try
        {
            checks = await doctor.RunAsync(loaded.Options, doctorContext, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            console.MarkupLine("[yellow]사용자가 실행을 중단했습니다.[/]");
            return ExitCodes.UserCancelled;
        }

        WriteTable(checks);

        console.WriteLine();
        console.MarkupLine("[bold]서버 관리자 확인 사항[/]");
        console.MarkupLine(Markup.Escape(ReadChecklist()));

        var failed = checks.Any(c => c.Status == DoctorStatus.Fail);
        if (settings.Sanitize && !WriteSanitized(checks, settings.Out)) return ExitCodes.EnvError;
        return failed ? ExitCodes.EnvError : ExitCodes.Success;
    }

    private void WriteTable(IReadOnlyList<DoctorCheck> checks)
    {
        var table = new Table().AddColumns("점검", "상태", "내용", "조치");
        foreach (var c in checks)
        {
            var color = c.Status switch { DoctorStatus.Ok => "green", DoctorStatus.Warn => "yellow", _ => "red" };
            table.AddRow(
                new Markup(Markup.Escape(c.Name)),
                new Markup($"[{color}]{c.Status}[/]"),
                new Markup(Markup.Escape(c.Detail)),
                new Markup(Markup.Escape(c.Action ?? "")));
        }
        console.Write(table);
    }

    private bool WriteSanitized(IReadOnlyList<DoctorCheck> checks, string? outDir)
    {
        string jsonPath, mdPath;
        try
        {
            var dir = fs.Path.GetFullPath(outDir ?? fs.Directory.GetCurrentDirectory());
            fs.Directory.CreateDirectory(dir);
            var stem = fs.Path.Combine(dir, "pelly-doctor-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            // Never overwrite an earlier result from the same second.
            var suffix = "";
            for (var n = 2; fs.File.Exists(stem + suffix + ".json") || fs.File.Exists(stem + suffix + ".md"); n++)
                suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
            jsonPath = stem + suffix + ".json";
            mdPath = stem + suffix + ".md";
            fs.File.WriteAllText(jsonPath, DoctorSanitizer.ToJson(checks));
            fs.File.WriteAllText(mdPath, DoctorSanitizer.ToMarkdown(checks));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            console.MarkupLine(Markup.Escape($"비식별 결과를 저장할 수 없습니다: {ex.Message}"));
            return false;
        }
        console.MarkupLine(Markup.Escape($"비식별 결과를 저장했습니다: {jsonPath}"));
        console.MarkupLine(Markup.Escape($"비식별 결과를 저장했습니다: {mdPath}"));
        return true;
    }

    private static string ReadChecklist()
    {
        using var stream = typeof(DoctorCommand).Assembly.GetManifestResourceStream("PellyCoder.Cli.server-checklist.md")
            ?? throw new InvalidOperationException("Embedded resource not found: server-checklist.md");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
