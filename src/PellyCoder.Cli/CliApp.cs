using System.Reflection;
using PellyCoder.Cli.Commands;
using PellyCoder.Core;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli;

public static class CliApp
{
    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName("pelly");
        config.SetApplicationVersion(PellyInfo.Version);
        // An unknown option is an argument error (exit 4), not ignored: a typo like --dry-rum would turn a preview into a write.
        config.Settings.StrictParsing = true;
        config.AddCommand<CsCommand>("cs").WithDescription("C# 메서드 하나를 요청대로 수정합니다.");
        config.AddCommand<SqlCommand>("sql").WithDescription("SQL 저장 프로시저·함수·뷰 하나를 요청대로 수정합니다.");
        config.AddCommand<DoctorCommand>("doctor").WithDescription("Ollama 서버·모델·토크나이저 환경을 점검합니다.");
        config.AddCommand<ReportCommand>("report").WithDescription("실행 기록에서 KPI와 실패 유형을 집계합니다.");

        // Without a handler Spectre returns -1 (process exit 255) for parse errors; PRD FR-00 allows only 0–5.
        var settings = config.Settings;
        config.SetExceptionHandler((ex, resolver) =>
        {
            var console = resolver?.Resolve(typeof(IAnsiConsole)) as IAnsiConsole ?? settings.Console ?? AnsiConsole.Console;
            var shutdown = resolver?.Resolve(typeof(CancellationToken)) is CancellationToken t ? t : CancellationToken.None;
            return HandleException(ex, console, shutdown);
        });
    }

    /// <summary>Settings type of each command, for <see cref="FindOptionWithoutValue"/>; matches the commands added in <see cref="Configure"/>.</summary>
    public static readonly IReadOnlyDictionary<string, Type> CommandSettings = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["cs"] = typeof(CsSettings),
        ["sql"] = typeof(SqlSettings),
        ["doctor"] = typeof(DoctorSettings),
        ["report"] = typeof(ReportSettings),
    };

    /// <summary>
    /// The first value option (e.g. <c>--out &lt;DIR&gt;</c>) written without a value: last on the line or followed by
    /// another option. Null when every value option has its value, or the command is unknown (Spectre reports that).
    /// </summary>
    /// <remarks>
    /// Checked on the raw arguments before Spectre parses them: under strict parsing Spectre fills a missing value with
    /// its internal "__default_command" token, so <c>report --sanitize --out</c> wrote to <c>./__default_command/</c>.
    /// Arguments after <c>--</c> are not options.
    /// </remarks>
    public static string? FindOptionWithoutValue(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !CommandSettings.TryGetValue(args[0], out var settingsType)) return null;
        var valueOptions = settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<CommandOptionAttribute>())
            .Where(a => a is { ValueName: not null, ValueIsOptional: false })
            .SelectMany(a => a!.LongNames)
            .Select(n => "--" + n)
            .ToHashSet(StringComparer.Ordinal);

        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            if (token == "--") return null;
            if (!valueOptions.Contains(token)) continue; // "--out=DIR" and "--out:DIR" carry their value
            if (i + 1 >= args.Count || args[i + 1].StartsWith('-')) return token;
            i++;
        }
        return null;
    }

    /// <summary>Exit 4 with a message naming the option when <see cref="FindOptionWithoutValue"/> finds one; otherwise null.</summary>
    public static int? RejectOptionWithoutValue(IReadOnlyList<string> args, IAnsiConsole console)
    {
        if (FindOptionWithoutValue(args) is not { } option) return null;
        console.MarkupLine($"[red]{Markup.Escape(option)} 옵션에 값이 없습니다.[/]");
        return ExitCodes.EnvError;
    }

    /// <summary>Exit code for an exception that escaped a command or came from argument parsing.</summary>
    /// <remarks>An <see cref="OperationCanceledException"/> is a user cancel (2) only when the shared shutdown token is cancelled; otherwise it is unexpected (4).</remarks>
    public static int HandleException(Exception ex, IAnsiConsole console, CancellationToken shutdown)
    {
        if (ex is OperationCanceledException && shutdown.IsCancellationRequested)
        {
            console.MarkupLine("[yellow]사용자가 실행을 중단했습니다.[/]");
            return ExitCodes.UserCancelled;
        }
        console.MarkupLine(ex is OperationCanceledException
            ? $"[red]예기치 않은 오류로 중단합니다: {Markup.Escape(ex.GetType().Name)}: {Markup.Escape(ex.Message)}[/]"
            : $"[red]{Markup.Escape(ex.Message)}[/]");
        return ExitCodes.EnvError;
    }

    /// <summary>Backstop for a result outside 0–5 (Spectre uses negative codes for its own failures): treated as an argument error.</summary>
    public static int NormalizeExitCode(int code) => code is >= ExitCodes.Success and <= ExitCodes.SafetyRejected ? code : ExitCodes.EnvError;
}
