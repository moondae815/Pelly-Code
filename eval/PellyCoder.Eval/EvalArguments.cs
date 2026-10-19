namespace PellyCoder.Eval;

/// <summary>
/// Command line of every subcommand; <see cref="Error"/> is set for unknown options, missing values or a wrong number of
/// positional arguments. A first argument naming a subcommand becomes <see cref="Command"/> (default <c>run</c>); each
/// subcommand accepts its own options only.
/// </summary>
public sealed record EvalArguments(string? Tasks, string? Kind, string? Out, string? Config, bool Help, string? Error, int Repeat = 1)
{
    /// <summary><c>--keep-work</c>: which work copies to keep.</summary>
    public KeepWork KeepWork { get; init; } = KeepWork.All;

    public const string Run = "run";
    public const string Validate = "validate";
    public const string Compare = "compare";

    /// <summary><c>run</c> (default), <c>validate</c> or <c>compare</c>.</summary>
    public string Command { get; init; } = Run;

    /// <summary><c>--suite</c>: the suite folder (holding <c>suite.json</c>); null: the repository's built-in suite.</summary>
    public string? Suite { get; init; }

    /// <summary>Arguments that are not options (<c>compare</c>: the two summary files).</summary>
    public IReadOnlyList<string> Positional { get; init; } = [];

    public const string Usage = """
        사용법: dotnet run --project eval/PellyCoder.Eval -- [옵션]

          --suite <폴더>    스위트 폴더(suite.json이 있는 곳, 기본: 저장소의 eval/benchmark)
          --tasks <패턴>    작업 id 접두사 또는 글롭(*, ?). 쉼표로 여러 개 (예: cs-modern, sql-00?, cs-legacy-001,sql)
          --kind <cs|sql>   작업 종류
          --out <폴더>      출력 폴더 (기본: 스위트의 resultsDir/<yyyyMMdd-HHmmss>/, 내장 스위트는 eval/results/)
          --config <파일>   pelly.json (사용자 설정과 환경 변수 위에 적용; SQL 허용 목록은 여기서 지정)
          --repeat <N>      같은 작업 집합을 N회 실행 (기본 1). N>1이면 회차별 결과는 run-<k>/, 상위에 summary.md·summary.json
          --keep-work <all|failed|none>  작업 복사본 보존 (기본 all; 현장 실제 코드에서는 failed 권장)
          --help            이 도움말

        검사: dotnet run --project eval/PellyCoder.Eval -- validate [--suite <폴더>] [--config <pelly.json>]
          실행 전에 작업 정의(id·판정·대상 파일·프로젝트 형식)를 점검합니다. LLM·SQL에 접속하지 않습니다.
          종료 코드 0 오류 없음(경고는 있어도 됨) · 4 오류 있음 또는 스위트를 읽을 수 없음

        비교: dotnet run --project eval/PellyCoder.Eval -- compare <기준선 summary.json> <후보 summary.json>
          R4 채택 규칙으로 판정합니다. 종료 코드 0 채택 · 3 거절 · 4 판정 불가

        SQL 연결은 환경 변수 PELLY_SQL_CONNECTION. 없거나 허용 목록에 없으면 sql 작업은 건너뜁니다.
        산출물: results.json, report.md, work/<id>/ (작업별 스위트 루트 복사본과 .pelly/runs 로그); --repeat N>1이면 회차마다 run-<k>/ 아래에
        종료 코드: 0 실행 완료 · 1 검증 불변 조건 위반 · 2 중단 · 4 설정 오류(스위트 오류 포함) 또는 LLM 서버 연결 불가
        """;

    /// <summary>Value options each subcommand accepts (<c>--help</c> is accepted everywhere).</summary>
    private static readonly Dictionary<string, string[]> Options = new(StringComparer.Ordinal)
    {
        [Run] = ["--suite", "--tasks", "--kind", "--out", "--config", "--repeat", "--keep-work"],
        [Validate] = ["--suite", "--config"],
        [Compare] = [],
    };

    /// <summary>Positional arguments each subcommand requires.</summary>
    private static readonly Dictionary<string, int> PositionalCount = new(StringComparer.Ordinal)
    {
        [Run] = 0,
        [Validate] = 0,
        [Compare] = 2,
    };

    public static EvalArguments Parse(IReadOnlyList<string> args)
    {
        var command = args.Count > 0 && Options.ContainsKey(args[0]) ? args[0] : Run;
        var start = args.Count > 0 && args[0] == command ? 1 : 0;
        var allowed = Options[command];
        string? tasks = null, kind = null, outDir = null, config = null, suite = null;
        var repeat = 1;
        var keepWork = KeepWork.All;
        var positional = new List<string>();

        EvalArguments Error(string message) => new(null, null, null, null, false, message) { Command = command };

        for (var i = start; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--help" or "-h") return new(null, null, null, null, true, null) { Command = command };
            if (!a.StartsWith('-') && positional.Count < PositionalCount[command])
            {
                positional.Add(a);
                continue;
            }
            if (!allowed.Contains(a, StringComparer.Ordinal))
                return Error($"알 수 없는 인자: {a}");
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return Error($"{a}에 값이 필요합니다");
            var value = args[++i];
            switch (a)
            {
                case "--suite": suite = value; break;
                case "--tasks": tasks = value; break;
                case "--kind":
                    if (value is not ("cs" or "sql")) return Error($"--kind는 cs 또는 sql: {value}");
                    kind = value;
                    break;
                case "--out": outDir = value; break;
                case "--repeat":
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out repeat) || repeat < 1)
                        return Error($"--repeat는 1 이상의 정수: {value}");
                    break;
                case "--keep-work":
                    switch (value)
                    {
                        case "all": keepWork = KeepWork.All; break;
                        case "failed": keepWork = KeepWork.Failed; break;
                        case "none": keepWork = KeepWork.None; break;
                        default: return Error($"--keep-work는 all, failed, none 중 하나: {value}");
                    }
                    break;
                default: config = value; break;
            }
        }

        if (positional.Count != PositionalCount[command])
            return Error(command == Compare
                ? "compare에는 <기준선 summary.json> <후보 summary.json> 두 개가 필요합니다"
                : $"{command}: 인자 {PositionalCount[command]}개가 필요합니다");
        return new(tasks, kind, outDir, config, false, null, repeat) { Command = command, Suite = suite, KeepWork = keepWork, Positional = positional };
    }
}
