# M3b 튜닝 (R4 채택 규칙, 후보 3개)

* **날짜:** 2026-10-18
* **결론:** 채택한 후보가 없다. 기본값과 프롬프트는 바뀌지 않는다. M3b의 최종 KPI는 기준선([m3b-baseline.md](m3b-baseline.md))이다.
* **기준선 요약:** [m3b-baseline.summary.json](m3b-baseline.summary.json) (평균 47.67 · cs-legacy 18.00 · cs-modern 9.67 · sql 20.00 · 시간 중위 1.90초 · 위반 0/0/0)
* **후보 요약:** [m3b-tuning/](m3b-tuning/)의 `*.summary.json` 셋. 경로·호스트·연결 문자열은 들어 있지 않다.
* **최종 요약:** 채택이 없어서 별도의 `m3b-final.summary.json`을 만들지 않았다. 최종 요약은 기준선 요약 그대로다.

## 1. 채택 규칙(R4)과 `compare`

* 구현: `eval/PellyCoder.Eval/Acceptance.cs`
* 네 규칙을 모두 만족해야 채택한다.
  1. 3회 평균 성공 작업 수가 기준선보다 1.0건 이상 많다.
  2. 어떤 범주(cs-legacy·cs-modern·sql, 작업 id 접두사)도 평균이 0.5건 넘게 줄지 않는다.
  3. 후보의 3회 모두 검증 불변 조건 위반이 0건이다.
  4. 작업 시간 중위값이 기준선의 1.5배 이하다.
* 명령: `dotnet run --project eval/PellyCoder.Eval -- compare <기준선 summary.json> <후보 summary.json>`
  * 종료 코드: 0 채택 · 3 거절 · 4 판정 불가
  * 규칙마다 ✓/✗ 줄과 수치를 출력한다. 서로 다른 실행 조건은 표로 나란히 보여 준다.
* 판정을 거부하는 경우(종료 4, 사유 출력):
  * 어느 한쪽이 중단된 반복 실행이다(`cancelled`).
  * 실행 조건·회차·범주가 없다.
  * 회차 수, 작업 집합, 회차별 실행 작업 수(`ran`)가 다르다.
  * 파일을 읽을 수 없다.
* `summary.json`에 `formatVersion`(현재 1)을 넣었다.
  * 읽을 때 필수 필드가 빠지면 기본값으로 채우지 않고 오류로 본다.
  * `formatVersion`이 없거나 다르면 읽지 않는다.
* Task 5의 기준선 요약에는 `formatVersion`이 없었다.
  * 읽기를 느슨하게 하지 않았다. 대신 새 코드의 `RepeatSummary.Build`로 `run-1~3/results.json`에서 다시 만들었다.
  * 실행 조건은 `results.json`에 없어서 옛 요약의 `conditions`를 그대로 옮겼다.
  * 다시 만든 요약은 `formatVersion`을 빼면 옛 파일과 JSON으로 같다.
  * 결과 폴더의 옛 파일은 `summary.task5.json`으로 남겼다.

## 2. 실험 조건

* 환경·명령·SQL은 기준선과 같다. 모델 `qwen3.6:35b-a3b-coding-nvfp4`, 50건 × 3회(`--repeat 3`).
* 후보는 서로 독립으로 기준선과 비교했다.
* 후보 2·3은 저장소 밖 `--config` 파일로 바꿨다. 제품 기본값은 건드리지 않았다.
* 후보 4는 프롬프트 리소스를 바꿔야 해서 작업 트리에서 쟀다. 거절되어 커밋 전에 되돌렸다. 잰 본문은 §5에 있다.
* SQL:
  * 후보마다 실행 직전에 `eval/corpus/Sql/deploy.sh`로 다시 배포했다.
  * 실행 전후 사용자 객체 48개의 이름·종류·`modify_date`·정의 체크섬·행 수를 비교했다. 세 후보 모두 차이가 없었다(DB 무변경).
* 후보 1(system.cs.txt에 「REQUEST가 요구한 동작을 없애지 말 것」 한 줄, cs-v3)은 평가하지 않았다.
  * M3a에서 이미 이 변형을 쟀고 더 나빴다(회피 8/14, 되돌림).
  * 벤치마크는 `--test`를 쓰지 않아서 이 후보가 이번 KPI를 움직일 수 없다.
  * 그래서 후보 1 채택 때만 하는 `--test` 모순 요청 회피율 재측정(M3a T14 방식 10회)도 하지 않았다.

## 3. 후보별 요약

| 후보 | 바꾼 조건 | 평균 (회차별) | cs-legacy | cs-modern | sql | 시간 중위 | 위반 | 판정 | 사유 |
| :--- | :--- | :--- | ---: | ---: | ---: | ---: | :--- | :--- | :--- |
| 기준선 | — | 47.67 (48·47·48) | 18.00 | 9.67 | 20.00 | 1.90초 | 0·0·0 | — | — |
| 2. think 768 | `budget.think` 512 → 768 | 47.67 (48·48·47) | 18.00 | 9.67 | 20.00 | 1.93초 | 0·0·0 | 거절 | 규칙 1(+0.00) |
| 3. code 샘플링 | code `temperature` 0.1 → 0.2, `topP` 0.9 → 0.95 | 47.00 (47·47·47) | 18.00 | 9.00 | 20.00 | 1.85초 | 0·0·0 | 거절 | 규칙 1(−0.67)·규칙 2(cs-modern −0.67) |
| 4. 예시 보강 | `fewshot.cs.txt`에 `[READ-ONLY: dto]` 예시(cs-v3) | 47.33 (48·47·47) | 17.67 | 9.67 | 20.00 | 1.94초 | 0·0·0 | 거절 | 규칙 1(−0.33) |

* 세 후보 모두 규칙 3(위반 0)·규칙 4(시간)는 만족했다. 판정을 가른 것은 규칙 1이다.
* 판정 도구 오류(`JudgeTooling`)는 모든 회차에서 0건이다.

## 4. 후보별 실패 작업의 변화

* 기준선의 실패: cs-modern-003 2/3, cs-modern-007 0/3, cs-modern-008 0/3. 원인은 셋 다 모델 실수다(기준선 문서 §4).
* 실패를 볼 때 수정본(`run-<k>/work/<id>/…/.pelly/runs/*/attempt-*/output.txt`)을 열어 확인했다.

### 후보 2 (think 768)

* 실패 작업과 모양이 기준선과 같다. cs-modern-003 2/3(회차 3에서 `ORDER BY IsGiftItem DESC`), 007·008 0/3.
* 새 실패는 없다.
* **이유:** 남은 실패 7건은 모두 1차 시도에서 pelly가 성공(종료 0)으로 끝낸 뒤 독립 판정에서 떨어졌다. 교정 시도가 없어서 THINK 단계가 한 번도 켜지지 않는다(think 토큰 0).
  * think 예산은 교정 시도에만 쓰인다. 그래서 이 실패들에 닿을 수 없다.
  * THINK가 켜진 작업은 sql-011·sql-018뿐이다(기준선과 같음). 768에서도 회차 1에서 둘 다, 회차 2에서 sql-018이 잘렸다. 결과는 기준선처럼 2차 시도 통과다.

### 후보 3 (code T 0.2 / topP 0.95)

* cs-modern-003이 2/3 → 0/3으로 나빠졌다. 3회 모두 `ORDER BY IsGiftItem DESC, OrderItemId`다. 사은품이 앞에 와서 요청과 반대다(기준선 회차 2의 실수가 매번 나왔다).
* 007·008은 기준선과 같은 모양으로 0/3이다(008은 3회 모두 `CommissionRate.Value > 0m`).
* cs-legacy-010은 회차 1에서 1차 출력이 대상 밖에 새 메서드를 만들었다. 교정 1회 뒤 통과해서 성공 수에는 영향이 없다.
* **이유:** 온도를 올리자 003의 경계(정렬 방향)에서 틀린 쪽이 더 자주 뽑혔다. 개선된 작업은 없다. S3(10건에서 차이 없음)와 달리 50건에서는 cs-modern이 0.67건 줄었다.
* repeatPenalty는 1.05 그대로 두었다(브리프 값은 temperature·topP만). S3의 변형 Y는 repeat 1.0이었다.

### 후보 4 (예시 보강, cs-v3)

* 007·008은 기준선과 같은 모양으로 0/3이다. cs-modern-003은 2/3 그대로다(회차 3 실패, 같은 `DESC` 실수).
* **새 실패:** cs-legacy-010 회차 2(`Failed(Judge)`, mustAdd '9' 2 → 2).
  * 요청: 「이미 취소된 주문(Status = 9)은 다시 바뀌지 않도록 UPDATE의 WHERE 조건에 추가」.
  * 수정본은 WHERE를 바꾸지 않았다. Dapper 매개변수 객체에 `IsCancelled = status == OrderStatus.Cancelled`만 더했다. SQL에서 쓰지도 않는 값이라 요청을 수행하지 않은 것이다.
  * 새 예시(`_options.AllowNegative` 조건으로 감싸는 패턴)를 따라 「조건 값을 들고 오는」 쪽으로 기운 것으로 보인다. 한 회차뿐이라 단정하지 않는다.
* **이유:** 예시에 DTO 구간이 생겨도 남은 실패(경계값 실수)와 관계가 없다. 대신 Legacy 1건이 한 번 흔들려 평균이 0.33 줄었다.

## 5. 후보 4로 잰 `fewshot.cs.txt` 본문 (재현용, 채택 안 함)

* `system.cs.txt`는 첫 줄만 `# version: 3`으로 바꿨다(두 파일 버전이 같아야 함). 본문은 그대로다.
* 토큰 수는 `FewShot_TokenCount_Between200And260` 범위 안이었다. `Resources_VersionIsCsV2…` 시험만 버전 때문에 실패했다(되돌린 뒤 통과).

```text
# version: 3
Example (format only; never copy its code).

Example input:
### TARGET
Calculator.Add(int, int) — file: Calculator.cs
### CONTEXT
[READ-ONLY: fields]
private readonly ILogger _logger;
private readonly CalcOptions _options;
[READ-ONLY: dto]
class CalcOptions: bool AllowNegative; int? MaxResult
[READ-ONLY: signatures]
public int Subtract(int a, int b);
public double Divide(int a, int b);
[EDITABLE: usings]
using System;
using System.Collections.Generic;
[EDITABLE: target]
public int Add(int a, int b)
{
    return a + b;
}
### REQUEST
Unless AllowNegative is set, throw ArgumentOutOfRangeException when either argument is negative, using the parameter name.

Example output (exactly one block, nothing else):
<<<<<<< SEARCH
    return a + b;
=======
    if (!_options.AllowNegative)
    {
        if (a < 0)
            throw new ArgumentOutOfRangeException(nameof(a));
        if (b < 0)
            throw new ArgumentOutOfRangeException(nameof(b));
    }
    return a + b;
>>>>>>> REPLACE
```

## 6. 재현

* 결과 폴더(`.gitignore` 대상, 커밋 안 함): `eval/results/m3b-c2-think768/`, `eval/results/m3b-c3-code-t02/`, `eval/results/m3b-c4-fewshot-dto/`
* 후보 2 설정(저장소 밖 `--config`): `{"budget":{"think":768}}` + 기준선과 같은 모델·SQL 허용 목록
* 후보 3 설정: `{"sampling":{"code":{"temperature":0.2,"topP":0.95}}}` + 같은 모델·허용 목록
* 판정: `dotnet run --project eval/PellyCoder.Eval -- compare docs/m3b-baseline.summary.json docs/m3b-tuning/<후보>.summary.json` → 세 후보 모두 종료 3(거절)

## 7. 남은 실패에 대한 관찰

* 남은 실패(cs-modern-003·007·008)는 모두 「빌드는 되고 판정 테스트에서 떨어지는」 경계값 실수다. pelly는 1차 시도에서 성공으로 끝내므로 교정 경로(think·피드백)가 개입하지 않는다.
* R5 목록의 후보(think 예산·샘플링·예시)는 이 유형에 닿지 않았다. 이 유형을 줄이려면 `--test`로 동작 테스트를 교정 루프에 넣는 쪽이 맞다. 목록 밖이라 다음 마일스톤으로 넘긴다(R5).
