namespace PellyCoder.Eval;

/// <summary>Command line of the runner; <see cref="Error"/> is set for unknown options or missing values.</summary>
public sealed record EvalArguments(string? Tasks, string? Kind, string? Out, string? Config, bool Help, string? Error, int Repeat = 1)
{
    public const string Usage = """
        사용법: dotnet run --project eval/PellyCoder.Eval -- [옵션]

          --tasks <패턴>    작업 id 접두사 또는 글롭(*, ?). 쉼표로 여러 개 (예: cs-modern, sql-00?, cs-legacy-001,sql)
          --kind <cs|sql>   작업 종류
          --out <폴더>      출력 폴더 (기본: eval/results/<yyyyMMdd-HHmmss>/)
          --config <파일>   pelly.json (사용자 설정과 환경 변수 위에 적용; SQL 허용 목록은 여기서 지정)
          --repeat <N>      같은 작업 집합을 N회 실행 (기본 1). N>1이면 회차별 결과는 run-<k>/, 상위에 summary.md·summary.json
          --help            이 도움말

        비교: dotnet run --project eval/PellyCoder.Eval -- compare <기준선 summary.json> <후보 summary.json>
          R4 채택 규칙으로 판정합니다. 종료 코드 0 채택 · 3 거절 · 4 판정 불가

        SQL 연결은 환경 변수 PELLY_SQL_CONNECTION. 없거나 허용 목록에 없으면 sql 작업은 건너뜁니다.
        산출물: results.json, report.md, work/<id>/ (작업별 코퍼스 복사본과 .pelly/runs 로그); --repeat N>1이면 회차마다 run-<k>/ 아래에
        종료 코드: 0 실행 완료 · 1 검증 불변 조건 위반 · 2 중단 · 4 설정 오류 또는 LLM 서버 연결 불가
        """;

    public static EvalArguments Parse(IReadOnlyList<string> args)
    {
        string? tasks = null, kind = null, outDir = null, config = null;
        var repeat = 1;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--help" or "-h") return new(null, null, null, null, true, null);
            if (a is not ("--tasks" or "--kind" or "--out" or "--config" or "--repeat"))
                return new(null, null, null, null, false, $"알 수 없는 인자: {a}");
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return new(null, null, null, null, false, $"{a}에 값이 필요합니다");
            var value = args[++i];
            switch (a)
            {
                case "--tasks": tasks = value; break;
                case "--kind":
                    if (value is not ("cs" or "sql")) return new(null, null, null, null, false, $"--kind는 cs 또는 sql: {value}");
                    kind = value;
                    break;
                case "--out": outDir = value; break;
                case "--repeat":
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out repeat) || repeat < 1)
                        return new(null, null, null, null, false, $"--repeat는 1 이상의 정수: {value}");
                    break;
                default: config = value; break;
            }
        }
        return new(tasks, kind, outDir, config, false, null, repeat);
    }
}
