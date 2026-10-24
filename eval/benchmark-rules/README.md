# 프로젝트 규칙 측정 스위트 (`eval/benchmark-rules`)

PRD §12.6 "규칙 도입 효과"를 재는 스위트입니다. 작업 형식과 판정은 내장 스위트와 같습니다(`eval/benchmark/README.md`).

## 무엇을 재나

* 코퍼스의 관례 중 **대상 코드만 봐서는 알 수 없는 것** 하나를 골랐습니다: SQL의 현재 시각은 `GETDATE()`가 아니라 `SYSDATETIME()`을 씁니다(Modern의 Dapper SQL과 SQL 객체 모두). 아래 작업의 대상에는 시각 함수가 없어서, 모델은 규칙이 없으면 관례를 볼 곳이 없습니다.
* 작업 6건(SQL 4 · Modern 2)은 모두 현재 시각이 필요한 요청이고, `mustContain: SYSDATETIME()`과 `mustNotAdd: GETDATE(`로 관례를 판정합니다.
* Legacy는 `GETDATE()`를 쓰는 다른 관례라 넣지 않았습니다.

## 규칙 파일

* 코퍼스는 내장 스위트와 공유하고, 규칙은 이 스위트에서만 넣습니다.
  * Modern: `roots[].overlay`(`rules/Modern/AGENTS.md`)로 루트 복사본에 넣습니다. Modern은 `Modern.sln`이 있는 루트가 작업 공간입니다.
  * Sql: `.sln`이 없어 작업 폴더 맨 위가 작업 공간이므로 `supportFiles`(`rules/Sql/AGENTS.md`)로 넣습니다.
* 내용은 `pelly rules init` 초안에 팀 관례 한 줄(`SYSDATETIME`)을 사람이 더한 것입니다. 현장에서 규칙 파일을 만드는 순서와 같습니다.

## 실행

같은 스위트를 규칙 없이(`--no-rules`, 작업 복사본의 `AGENTS.md`를 모두 지움) 한 번, 규칙과 함께 한 번 돌려 비교합니다.

```bash
dotnet run --project eval/PellyCoder.Eval -- --suite eval/benchmark-rules --config <pelly.json> --repeat 3 --no-rules --out <규칙 없음 폴더>
dotnet run --project eval/PellyCoder.Eval -- --suite eval/benchmark-rules --config <pelly.json> --repeat 3 --out <규칙 있음 폴더>
dotnet run --project eval/PellyCoder.Eval -- compare <규칙 없음>/summary.json <규칙 있음>/summary.json
```

결과 기록: `docs/v2-3-rules-premeasure.md`.
