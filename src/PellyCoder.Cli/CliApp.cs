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
        config.AddCommand<DoctorCommand>("doctor").WithDescription("Ollama 서버·모델·토크나이저 환경을 점검합니다.");
        config.AddCommand<ApplyCommand>("apply").WithDescription("명세 파일(changes/<id>.md)의 작업을 순서대로 실행합니다.");
        config.AddCommand<ReportCommand>("report").WithDescription("실행 기록에서 KPI와 실패 유형을 집계합니다.");
        config.AddCommand<CompletionCommand>("completion").WithDescription("셸 자동완성 스크립트를 출력합니다 (PowerShell).");
        // "pelly sql <FILE> [REQUEST]" stays the edit command (the branch default); a file named pull or check is written ./pull.
        config.AddBranch("sql", b =>
        {
            b.SetDescription("SQL 저장 프로시저·함수·뷰 하나를 요청대로 수정합니다.");
            b.SetDefaultCommand<SqlCommand>();
            // Spectre's branch help shows no arguments of the default command, so the edit usage is given as examples.
            b.AddExample("sql", "<FILE>", "[요청문]", "[--object <이름>]", "[--line <N>]", "[--dry-run]", "[--yes]");
            b.AddExample("sql", "db/usp_X.sql", "\"요청문\"", "--object", "dbo.usp_X");
            b.AddCommand<SqlPullCommand>("pull").WithDescription("서버의 객체 정의를 파일로 가져옵니다.");
            b.AddCommand<SqlCheckCommand>("check").WithDescription("SQL 파일의 객체를 서버와 대조합니다.");
        });

        config.AddBranch("rules", b =>
        {
            b.SetDescription("AGENTS.md의 Pelly rules 절을 다룹니다.");
            b.SetDefaultCommand<RulesUsageCommand>();
            b.AddCommand<RulesCheckCommand>("check").WithDescription("AGENTS.md의 Pelly rules 절을 검사합니다.");
            b.AddCommand<RulesInitCommand>("init").WithDescription("코드베이스를 분석해 AGENTS.md에 규칙 초안을 만듭니다.");
        });

        config.AddBranch("spec", b =>
        {
            b.SetDescription("작업 명세 파일을 다룹니다.");
            b.SetDefaultCommand<SpecUsageCommand>();
            b.AddCommand<SpecNewCommand>("new").WithDescription("빈 명세 파일 changes/<id>.md를 만듭니다.");
            b.AddCommand<SpecCheckCommand>("check").WithDescription("명세 파일의 문법·대상·이름을 검사합니다.");
        });

        config.AddBranch("ui", b =>
        {
            b.SetDescription("작은 창에서 요청·진행·diff 승인을 합니다.");
            b.SetDefaultCommand<UiUsageCommand>();
            b.AddCommand<UiCsCommand>("cs").WithDescription("C# 메서드 하나를 창에서 요청·승인해 수정합니다.");
            b.AddCommand<UiSqlCommand>("sql").WithDescription("SQL 객체 하나를 창에서 요청·승인해 수정합니다.");
        });

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
        ["sql pull"] = typeof(SqlPullSettings),
        ["sql check"] = typeof(SqlCheckSettings),
        ["doctor"] = typeof(DoctorSettings),
        ["report"] = typeof(ReportSettings),
        ["apply"] = typeof(ApplySettings),
        ["spec new"] = typeof(SpecNewSettings),
        ["spec check"] = typeof(SpecCheckSettings),
        ["rules check"] = typeof(RulesCheckSettings),
        ["rules init"] = typeof(RulesInitSettings),
        ["ui cs"] = typeof(CsSettings),
        ["ui sql"] = typeof(SqlSettings),
        ["completion"] = typeof(CompletionSettings),
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
        if (args.Count == 0) return null;
        // "rules", "spec" and "ui" are branches: the command is "<branch> <sub>" and its options start at index 2.
        // "sql" is a branch whose default command is the edit: "sql pull" and "sql check" are subcommands, anything else is "sql".
        var isSqlSub = args[0] == "sql" && args.Count >= 2 && (args[1] is "pull" or "check");
        var isBranch = args[0] is "rules" or "spec" or "ui" || isSqlSub;
        var first = isBranch ? 2 : 1;
        var key = isBranch ? (args.Count >= 2 ? args[0] + " " + args[1] : null) : args[0];
        if (key is null || !CommandSettings.TryGetValue(key, out var settingsType)) return null;
        var valueOptions = OptionNames(settingsType, valueOnly: true).Select(n => "--" + n).ToHashSet(StringComparer.Ordinal);

        for (var i = first; i < args.Count; i++)
        {
            var token = args[i];
            if (token == "--") return null;
            if (!valueOptions.Contains(token)) continue; // "--out=DIR" and "--out:DIR" carry their value
            if (i + 1 >= args.Count || args[i + 1].StartsWith('-')) return token;
            i++;
        }
        return null;
    }

    /// <summary>Exit 4 with <c>Unknown option 'x'.</c> when <see cref="FindUnknownSqlOption"/> finds one; otherwise null.</summary>
    public static int? RejectUnknownSqlOption(IReadOnlyList<string> args, IAnsiConsole console)
    {
        if (FindUnknownSqlOption(args) is not { } option) return null;
        console.MarkupLine($"[red]{Markup.Escape($"Unknown option '{option}'.")}[/]");
        return ExitCodes.EnvError;
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

    /// <summary>Long option names (without <c>--</c>) of a settings type; with <paramref name="valueOnly"/>, only those that require a value.</summary>
    private static IEnumerable<string> OptionNames(Type settingsType, bool valueOnly) =>
        OptionAttributes(settingsType)
            .Where(a => !valueOnly || a is { ValueName: not null, ValueIsOptional: false })
            .SelectMany(a => a.LongNames);

    /// <summary>A command's visible long options (hidden ones left out), for shell completion.</summary>
    internal static IReadOnlyList<OptionInfo> CommandOptions(Type settingsType) =>
        OptionAttributes(settingsType)
            .Where(a => !a.IsHidden)
            .SelectMany(a => a.LongNames.Select(n => new OptionInfo(n, a.ValueName is not null)))
            .ToList();

    private static IEnumerable<CommandOptionAttribute> OptionAttributes(Type settingsType) =>
        settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<CommandOptionAttribute>())
            .OfType<CommandOptionAttribute>();

    /// <summary>
    /// The checks Program runs on the raw arguments before Spectre parses them (a value option without its value, an
    /// unknown <c>sql</c> option): the exit code to stop with, or null to let Spectre run.
    /// </summary>
    public static int? PreParseReject(IReadOnlyList<string> args, IAnsiConsole console) =>
        RejectOptionWithoutValue(args, console) ?? RejectUnknownSqlOption(args, console);

    /// <summary>The whole command line as Program runs it: pre-parse checks, then Spectre, then <see cref="NormalizeExitCode"/>.</summary>
    public static async Task<int> RunAsync(CommandApp app, string[] args, IAnsiConsole console) =>
        PreParseReject(args, console) ?? NormalizeExitCode(await app.RunAsync(args));

    /// <summary>
    /// The first <c>--option</c> of a <c>sql</c> command line that the command does not have, or null. Spectre parses
    /// the branch default command (<c>pelly sql &lt;FILE&gt;</c>) in two steps and reports any failure there as
    /// <c>Unknown command '&lt;FILE&gt;'</c>, which hides a mistyped option; checking the raw arguments first keeps the message.
    /// </summary>
    public static string? FindUnknownSqlOption(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "sql") return null;
        var settingsType = args.Count >= 2 && args[1] is "pull" or "check" ? CommandSettings["sql " + args[1]] : typeof(SqlSettings);
        var known = OptionNames(settingsType, valueOnly: false).Append("help").ToHashSet(StringComparer.Ordinal);
        foreach (var token in args.Skip(1))
        {
            if (token == "--") break;
            if (!token.StartsWith("--", StringComparison.Ordinal)) continue;
            var name = token[2..].Split('=', ':')[0];
            if (!known.Contains(name)) return name;
        }
        return null;
    }

    /// <summary>Backstop for a result outside 0–5 (Spectre uses negative codes for its own failures): treated as an argument error.</summary>
    public static int NormalizeExitCode(int code) => code is >= ExitCodes.Success and <= ExitCodes.SafetyRejected ? code : ExitCodes.EnvError;
}

/// <summary>One long option of a command: its name without <c>--</c> and whether a value follows it.</summary>
internal sealed record OptionInfo(string LongName, bool TakesValue);
