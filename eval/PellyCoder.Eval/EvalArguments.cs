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
    public const string Record = "record";
    public const string Export = "export";
    public const string Apply = "apply";

    /// <summary><c>run</c> (default), <c>validate</c>, <c>compare</c>, <c>record</c>, <c>export</c> or <c>apply</c>.</summary>
    public string Command { get; init; } = Run;

    /// <summary><c>--suite</c>: the suite folder (holding <c>suite.json</c>); null: the repository's built-in suite.</summary>
    public string? Suite { get; init; }

    /// <summary>Arguments that are not options (<c>compare</c>: the two summary files).</summary>
    public IReadOnlyList<string> Positional { get; init; } = [];

    /// <summary><c>record --gguf-sha256</c>: the model file's SHA-256 (64 hex digits, checked by the command).</summary>
    public string? GgufSha256 { get; init; }

    /// <summary><c>apply --cases</c>: case id prefixes or globs, comma-separated (the <c>--tasks</c> rules); null: every case.</summary>
    public string? Cases { get; init; }

    /// <summary><c>validate --deep</c>: also runs the judge tests on the original sources.</summary>
    public bool Deep { get; init; }

    /// <summary>Hidden <c>--offline-selftest</c> (run only, not in <see cref="Usage"/>): the packaged exe's bundled-component check.</summary>
    public bool OfflineSelfTest { get; init; }

    /// <summary><c>run --no-rules</c>: delete every <c>AGENTS.md</c> from the work copies (the project rules' control run).</summary>
    public bool NoRules { get; init; }

    public const string Usage = """
        사용법: dotnet run --project eval/PellyCoder.Eval -- [옵션]

          --suite <폴더>    스위트 폴더(suite.json이 있는 곳, 기본: 저장소의 eval/benchmark)
          --tasks <패턴>    작업 id 접두사 또는 글롭(*, ?). 쉼표로 여러 개 (예: cs-modern, sql-00?, cs-legacy-001,sql)
          --kind <cs|sql>   작업 종류
          --out <폴더>      출력 폴더 (기본: 스위트의 resultsDir/<yyyyMMdd-HHmmss>/, 내장 스위트는 eval/results/). 비어 있지 않으면 거절(종료 4)
          --config <파일>   pelly.json (사용자 설정과 환경 변수 위에 적용; SQL 허용 목록은 여기서 지정)
          --repeat <N>      같은 작업 집합을 N회 실행 (기본 1). N>1이면 회차별 결과는 run-<k>/, 상위에 summary.md·summary.json
          --keep-work <all|failed|none>  작업 복사본 보존 (기본 all; 현장 실제 코드에서는 failed 권장)
          --no-rules                     작업 복사본에서 AGENTS.md를 모두 지움 (규칙 없이 재는 대조 실행)
          --help            이 도움말

        검사: dotnet run --project eval/PellyCoder.Eval -- validate [--suite <폴더>] [--config <pelly.json>]
          실행 전에 작업 정의(id·판정·대상 파일·프로젝트 형식)를 점검합니다. LLM·SQL에 접속하지 않습니다.
          --deep            판정 테스트를 원본에서 실제로 실행합니다(tests는 실패, failingTests는 통과해야 함). 정적 검사에 오류가 없을 때만, 복원·빌드가 필요해 느립니다
          종료 코드 0 오류 없음(경고는 있어도 됨) · 4 오류 있음 또는 스위트를 읽을 수 없음

        비교: dotnet run --project eval/PellyCoder.Eval -- compare <기준선 summary.json> <후보 summary.json>
          R4 채택 규칙으로 판정합니다. 두 요약의 모델 digest나 Ollama 버전이 다르면 판정 불가(4)입니다.
          종료 코드 0 채택 · 3 거절 · 4 판정 불가

        기록: dotnet run --project eval/PellyCoder.Eval -- record --out <파일> [--gguf-sha256 <hex>] [--config <pelly.json>]
          서버의 Ollama 버전과 모델 식별 정보(digest·형식·양자화)를 JSON으로 씁니다. 접속 주소는 쓰지 않습니다.
          종료 코드 0 기록함 · 3 모델이 서버에 없거나 서버가 버전·digest를 알리지 않음 · 4 접속 불가·인자 오류·파일 쓰기 실패

        반출: dotnet run --project eval/PellyCoder.Eval -- export <결과 폴더> --out <폴더>
          결과 폴더(단일 실행 results.json 또는 --repeat의 summary.json + run-<k>/)를 비식별해 --out에 씁니다. KPI 필드는 그대로,
          사유·검증 상세·생략 사유 본문과 접속 주소·경로는 지웁니다. 작업 복사본(work/)과 .pelly/runs는 복사하지 않습니다(pelly 실행 기록은 pelly report --sanitize).
          작업 id가 cs-modern-NNN · cs-legacy-NNN · sql-NNN 형식이 아니면 반출하지 않습니다.
          종료 코드 0 반출함 · 4 입력 없음·--out이 비어 있지 않음·id 형식 오류·인자 오류

        적용 벤치마크(pelly-eval apply): dotnet run --project eval/PellyCoder.Eval -- apply [--cases <패턴>] [--repeat <N>] [--out <폴더>] [--config <pelly.json>]
          eval/benchmark/apply/*.md(승인된 S6 정답 명세 12건)를 사례·회차마다 eval/corpus 복사본(work/<id>-<n>/corpus/)에서 pelly apply로 끝까지 실행합니다.
          --cases <패턴>    사례 id 접두사 또는 글롭(*, ?). 쉼표로 여러 개 (예: a1,d*)
          Legacy 범위 사례는 Windows에서만, Sql 범위 사례는 SQL 연결이 있을 때만 실행하고 아니면 건너뜁니다.
          종료 0인 사례는 바뀐 C# 파일의 프로젝트와 그 의존자를 다시 빌드합니다(독립 검증).
          산출물: apply-results.json, apply-summary.md(명세 완료율·작업 3~8개 완료율·그룹 빌드 통과율·건너뛴 사례), work/<id>-<n>/
          종료 코드: 0 실행 완료 · 1 검증 불변 조건 위반(종료 0인데 독립 빌드에 컴파일러 오류) · 2 중단 · 4 설정 오류 또는 LLM 서버 연결 불가

        SQL 연결은 환경 변수 PELLY_SQL_CONNECTION. 없거나 허용 목록에 없으면 sql 작업은 건너뜁니다.
        산출물: results.json, report.md, work/<id>/ (작업별 스위트 루트 복사본과 .pelly/runs 로그); --repeat N>1이면 회차마다 run-<k>/ 아래에
        id 형식이 틀리거나 대상을 해석하지 못한 작업은 실행하지 않고 실패(Definition)로 기록합니다. 작업 YAML을 읽을 수 없으면 종료 4.
        종료 코드: 0 실행 완료 · 1 검증 불변 조건 위반 · 2 중단 · 4 설정 오류(스위트·작업 파일 오류 포함) 또는 LLM 서버 연결 불가
        """;

    /// <summary>Options each subcommand accepts (<c>--help</c> is accepted everywhere); <see cref="Flags"/> take no value.</summary>
    private static readonly Dictionary<string, string[]> Options = new(StringComparer.Ordinal)
    {
        [Run] = ["--suite", "--tasks", "--kind", "--out", "--config", "--repeat", "--keep-work", "--offline-selftest", "--no-rules"],
        [Validate] = ["--suite", "--config", "--deep"],
        [Compare] = [],
        [Record] = ["--out", "--gguf-sha256", "--config"],
        [Export] = ["--out"],
        [Apply] = ["--cases", "--repeat", "--out", "--config"],
    };

    private static readonly string[] Flags = ["--deep", "--offline-selftest", "--no-rules"];

    /// <summary>Positional arguments each subcommand requires.</summary>
    private static readonly Dictionary<string, int> PositionalCount = new(StringComparer.Ordinal)
    {
        [Run] = 0,
        [Validate] = 0,
        [Compare] = 2,
        [Record] = 0,
        [Export] = 1,
        [Apply] = 0,
    };

    public static EvalArguments Parse(IReadOnlyList<string> args)
    {
        var command = args.Count > 0 && Options.ContainsKey(args[0]) ? args[0] : Run;
        var start = args.Count > 0 && args[0] == command ? 1 : 0;
        var allowed = Options[command];
        string? tasks = null, kind = null, outDir = null, config = null, suite = null, gguf = null, cases = null;
        var deep = false;
        var selfTest = false;
        var noRules = false;
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
            if (Flags.Contains(a, StringComparer.Ordinal))
            {
                if (a == "--deep") deep = true;
                else if (a == "--no-rules") noRules = true;
                else selfTest = true;
                continue;
            }
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return Error($"{a}에 값이 필요합니다");
            var value = args[++i];
            switch (a)
            {
                case "--suite": suite = value; break;
                case "--tasks": tasks = value; break;
                case "--cases": cases = value; break;
                case "--kind":
                    if (value is not ("cs" or "sql")) return Error($"--kind는 cs 또는 sql: {value}");
                    kind = value;
                    break;
                case "--out": outDir = value; break;
                case "--gguf-sha256": gguf = value; break;
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

        if (command == Record && outDir is null) return Error("record에는 --out <파일>이 필요합니다");
        if (command == Export && positional.Count == 1 && outDir is null) return Error("export에는 --out <폴더>가 필요합니다");
        if (positional.Count != PositionalCount[command])
            return Error(command == Compare
                ? "compare에는 <기준선 summary.json> <후보 summary.json> 두 개가 필요합니다"
                : command == Export ? "export에는 <결과 폴더> 하나가 필요합니다"
                : $"{command}: 인자 {PositionalCount[command]}개가 필요합니다");
        return new(tasks, kind, outDir, config, false, null, repeat) { Command = command, Suite = suite, Deep = deep, OfflineSelfTest = selfTest, NoRules = noRules, GgufSha256 = gguf, KeepWork = keepWork, Positional = positional, Cases = cases };
    }
}
