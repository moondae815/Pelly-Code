# 사용 가이드

pelly는 C# 메서드 하나나 SQL 저장 프로시저·함수·뷰 하나를 요청대로 수정하고, 컴파일러와 DB 엔진으로 검증합니다.
설치는 [install.md](install.md)를 먼저 보십시오.

## 기본 흐름

1. 대상과 요청문을 줍니다.
2. 모델이 수정안을 만듭니다. pelly가 파일에 먼저 쓰고 검증합니다.
3. 검증을 통과하면 diff를 보여 주고 승인을 묻습니다. 거부하면 원래 파일로 되돌립니다.

승인 질문이 떠 있는 동안 파일은 이미 바뀐 상태입니다. IDE나 파일 감시 도구가 그 변경을 볼 수 있습니다.

## `pelly cs` — C# 멤버 하나 수정

```
pelly cs <파일> ["<요청문>"] [--class <클래스>] [--method <메서드>] [--line N]
         [--yes] [--dry-run] [--max-retries N] [--strict-baseline] [--warn-as-error]
         [--test] [--verbose] [--pause-on-exit]
         [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
```

**대상 지정 (3가지)**

1. `--method <이름>` (+ 선택 `--class <클래스>`). 같은 이름이 여러 개면 `--class`로 좁힙니다.
2. `--line N`. 그 줄을 포함하는 멤버를 고칩니다. IDE 커서 줄을 넘길 때 씁니다. `--class`/`--method`와 함께 쓸 수 없고 1 이상이어야 합니다.
3. 둘 다 생략. 요청문에 나온 식별자로 찾고, 없으면 모델이 멤버 목록(최대 60개)에서 고릅니다.

요청문을 생략하면 대화형 실행에서는 입력을 묻고, 입력이 리디렉션된 실행에서는 종료 코드 4입니다.

```
pelly cs Shop.Api\Services\OrderService.cs "배송 중인 주문은 취소할 수 없게 해줘" --method CancelAsync --test --verbose
pelly cs Shop.Api\Services\OrderService.cs "null 검사 추가" --line 42 --pause-on-exit
pelly cs Shop.Api\Services\OrderService.cs
```

* 모델은 대상 멤버 안을 고치면서 private 도우미 메서드를 추가할 수 있습니다. 시그니처 변경과 다른 멤버 수정은 거부되어 재시도됩니다.
* 요청문이나 대상 파일에 모델 제어 토큰 문자열(`<|im_end|>` 등)이 있으면 서버를 부르기 전에 종료 코드 4로 멈춥니다.
* 파일에 CRLF와 LF가 섞여 있으면 알림을 보여 주고, 수정하면 많은 쪽으로 통일됩니다.

## `pelly sql` — 저장 프로시저·함수·뷰 하나 수정

```
pelly sql <파일.sql> ["<요청문>"] [--object <이름>] [--line N]
          [--yes] [--dry-run] [--max-retries N] [--verbose] [--pause-on-exit]
          [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
```

* 수정할 수 있는 것은 **대상 객체 배치 하나**입니다. 같은 파일의 다른 배치가 바뀌면 거부됩니다.
* 대상 지정: 파일에 `CREATE`/`ALTER PROCEDURE|FUNCTION|VIEW`가 하나면 자동입니다. 여럿이면 `--object dbo.이름` 또는 `--line N`으로 지정합니다. 지정이 없으면 대화형은 선택 메뉴, 비대화형은 종료 코드 3입니다. `--line`과 `--object`는 함께 쓸 수 없습니다.
* 검증은 DB 서버가 합니다. 참조 객체가 있는지 보고, `SET NOEXEC ON`으로 컴파일해 새 오류가 없는지 봅니다. 트랜잭션 안에서 하고 롤백하므로 객체는 실제로 바뀌지 않습니다. 원본에 이미 있던 오류는 세지 않습니다.
* 서버 연결이 필수입니다. 연결할 수 없으면 종료 코드 4입니다. 설정은 [install.md](install.md)의 4절을 보십시오.
* 검증 계정 권한은 DB 수준 `VIEW DEFINITION` 하나면 됩니다. 없으면 종료 코드 4와 안내 메시지로 멈춥니다.
* 쓰기 전에 안전 가드가 금지 구문을 검사합니다(`GRANT`/`REVOKE`/`DENY`, `xp_cmdshell`, `OPENROWSET`, `SET NOEXEC` 등). 걸리면 재시도하고, 끝내 못 고치면 종료 코드 5입니다. 원본 대상에 이미 금지 구문이 있으면 모델을 부르기 전에 종료 코드 5로 멈춥니다.

```
pelly sql Procedures\usp_CancelOrder.sql "이미 배송된 주문은 취소하지 않고 오류를 내게 해줘"
pelly sql Settlement_All.sql "월 정산에 수수료 컬럼 추가" --object dbo.usp_SettleMonthly
pelly sql Settlement_All.sql "조건 추가" --line 120
```

## `pelly spec`·`pelly apply` — 작업 명세 파일과 순차 실행 (V2-1a)
```bash
pelly spec new <id> [--pause-on-exit]
pelly spec check <명세 파일> [--impact] [--config <경로>] [--pause-on-exit]
pelly apply <명세 파일> [--continue] [--resume] [--review-at-end] [--yes] [--dry-run] \
            [--max-retries N] [--verbose] [--config <경로>] [--pause-on-exit]
```
여러 파일에 걸친 변경을 `changes/<id>.md` 명세 파일에 작업 줄로 적고, 위에서부터 하나씩 `pelly cs`·`pelly sql`과 같은 검증 절차로 적용합니다.

```markdown
---
id: add-refund
status: draft
---
# 환불 추가

## 수용 기준
- AC1: 배송 완료 주문은 취소할 수 없다

## 작업
- [ ] T1 cs  src/Orders/OrderService.cs#OrderService.Cancel  배송 완료면 예외 던지기 (AC1)
- [ ] T2 sql db/sp/usp_CancelOrder.sql#dbo.usp_CancelOrder   배송 완료 제외 조건 추가
```

* **작업 줄:** `- [ ] <ID> <cs|sql> <파일>[#심볼] <요청> [(AC 참조)]`. 필드 사이는 공백 하나 이상입니다. 작업은 최대 20개입니다. 작업 줄은 제목이 정확히 `## 작업`인 절 아래에만 둡니다 — 다른 절(예: `## 작업 (2차)`)에 있는 작업 줄은 `InvalidTaskLine` 오류입니다.
* **`cs` 작업은 `#멤버`가 필수입니다.** 클래스가 둘 이상이거나 멤버가 겹치면 `#클래스.멤버`로 씁니다. `sql` 작업의 `#객체`는 선택입니다.
* **`pelly spec check`:** 문법, 대상 파일·심볼 존재, AC 참조, 작업 수 상한을 검사합니다. 오류가 있으면 종료 코드 4입니다. 작업 그룹 `[G1]`은 아래 `pelly apply`의 그룹 항목을 보십시오.
* **영향 탐색:** `spec check`는 명세에 작업 그룹이 있으면 미완료 그룹 `cs` 작업의 대상 멤버에서, `--impact`이면 미완료 `cs` 작업 전부에서 시작해 그 멤버(같은 이름의 오버로드 전부)를 구현·재정의하거나 부르거나 참조하는 곳, 그리고 그 멤버가 구현·재정의하는 기반 멤버와 그 멤버의 다른 구현·호출부를 찾습니다. 찾은 곳 `(파일, 멤버)`와 파일·멤버가 같은 작업(완료 작업 포함)이 명세에 없으면 찾은 곳마다 경고 `ImpactMissing`(`작업 T1의 영향 대상이 명세에 없습니다: <파일>#<멤버> (<이유>)`, 이유는 `구현`·`재정의`·`기반 멤버`·`호출`·`멤버 밖`)을 남깁니다. `pelly apply`도 시작할 때 그룹 `cs` 작업에 대해 같은 탐색을 하고, 다른 경고처럼 알린 뒤 계속합니다(대화형이면 계속할지 묻고, `--yes`이면 묻지 않습니다). 의미 분석(프로젝트를 불러 심볼로 찾기)을 쓸 수 없으면 이름 기준으로 대신하고 반드시 `ImpactNameBased`(`영향 탐색: 이름 기준(부정확): 의미 분석 실패 — <유형>`, 예: `복원 안 됨 (dotnet restore 필요)`, `해석률 97.2%`)를 남깁니다. 일부 폴더·파일을 읽지 못하면 `ImpactIncomplete`, 메서드·속성이 아닌 대상은 `ImpactUnsupported`, 탐색 자체가 실패하면 `ImpactFailed`입니다. `sql` 작업은 탐색하지 않습니다. 영향 탐색은 경고만 만들며 종료 코드를 바꾸지 않습니다. `apply.json`에는 `impact`(`semantic`·`name`, 탐색하지 않았으면 `null`)와 `impactFallback`(이름 기준으로 대신한 이유)을 적습니다.
* **`pelly apply`:** 작업마다 성공하면 줄을 `- [x] … <!-- run: <run-id> -->`로, 실패하면 `<!-- failed: <run-id> <종류> -->`로 표시하고 멈춥니다. `--continue`는 실패한 작업을 건너뛰고 나머지를 이어 가며(종료 코드는 1 — 작업별 검토에서 거부한 작업도 실패로 셉니다), `--resume`은 완료 표시가 된 작업을 건너뛰고 첫 미완료 작업부터 이어 갑니다. `[x]`가 있는 명세를 `--resume` 없이 실행하면 종료 코드 4입니다.
* **검토:** 기본은 작업마다 diff 승인을 묻습니다. `--review-at-end`는 전부 끝난 뒤 한 번만 묻고, 거부하면 이번 apply가 바꾼 파일을 모두 되돌리고 이번에 남긴 체크 표시와 상태도 되돌립니다(종료 코드 2). 실행 중 Ctrl+C로 취소해도 같은 방식으로 되돌립니다. 단, apply 도중 pelly 밖에서 바뀐 파일은 덮어쓰지 않고 그대로 두며, 그 파일을 알리고 종료 코드 4로 끝납니다. 그 파일의 작업은 `[x]`를 유지합니다. `--review-at-end`는 대화형 실행에서만 쓸 수 있습니다(비대화형이면 `--yes`가 있어도 시작 전에 종료 코드 4). 명세 파일을 쓸 수 없어 중간에 멈추면 파일을 되돌릴 수 없으므로, 검토하지 않은 변경이 남은 파일을 경고로 알려 줍니다.
* **작업 그룹(`[G1]`)** 은 시그니처·이름을 바꾸는 `cs` 작업을 묶어, 마지막 작업 뒤에 한 번만 빌드 검증합니다. 같은 그룹의 작업은 명세에서 연속해야 하고(`GroupNotContiguous`), `sql` 작업은 넣을 수 없습니다(`SqlInGroup`). 그룹 작업에서는 대상 멤버의 반환 형식·매개변수·한정자·특성·이름을 바꿀 수 있지만, 다른 멤버를 바꾸거나 지울 수 없고 using을 지울 수 없으며 public 멤버를 추가할 수 없습니다(private 도우미는 가능). 이름이 바뀐 대상은 같은 형식에 새로 생긴 같은 종류·같은 접근 수준의 멤버 중 같은 이름, 없으면 같은 자리(앞뒤에 그대로 있는 멤버로 셈)의 멤버로 짝짓습니다. private 대상 바로 옆(위나 아래)에 private 도우미도 새로 생겨 자리로 가를 수 없으면, 짝을 잘못 짓지 않도록 수정을 거절하고 도우미를 대상에서 떨어진 자리에 두라고 피드백합니다(시그니처가 그대로인 쪽을 고르지 않습니다 — 이름과 시그니처를 함께 바꾼 대상 옆에 옛 시그니처의 도우미가 생긴 경우와 구별되지 않기 때문입니다). 그룹의 첫 작업 전에 영향 범위(그룹 파일의 프로젝트와, 그 프로젝트를 직접·간접으로 `ProjectReference`하는 프로젝트 — 후보는 작업 공간 폴더에서 찾은 `.csproj`와 작업 공간의 `.sln`·`.slnx`에 적힌 프로젝트이며, `packages` 폴더는 모든 하위 폴더에 `.nupkg`가 있는 packages.config 복원 폴더일 때만 건너뜁니다)를 기준 빌드하고, 마지막 작업 뒤에 다시 빌드해 새로 생긴 오류만 셉니다(그룹 빌드는 `GenerateFullPaths=true`로 오류 경로를 전체 경로로 받고, 상대 경로로 나온 오류는 그 프로젝트 폴더 기준으로 풉니다). 새 오류가 그룹 작업의 대상 멤버 안이거나, 그 작업이 더한 private 도우미 안이거나, 그 작업 대상이 속한 형식의 선언 줄·기본 형식 목록(예: 인터페이스를 더 이상 구현하지 않는 `CS0535`·`CS0738`)이면 그 작업이 빌드 피드백을 받아 다시 실행됩니다(그룹 전체 `retry.max` 또는 `--max-retries`; 재실행에서는 그 작업이 더한 도우미도 고칠 수 있습니다). 재실행을 다 써도 새 오류가 남으면 그룹 전체를 되돌리고 종료 코드 1(`GroupBuildFailed`)입니다. 그 밖의 오류(다른 멤버, 멤버 밖, 그룹 밖 파일)는 명세에 빠진 영향 대상이므로 그룹 전체를 되돌리고 위치를 알린 뒤 종료 코드 1(`GroupMissingTarget`)입니다. 위치는 `<파일>#<멤버>`, 멤버 밖이거나 파일을 읽을 수 없으면 `<파일>:<줄>`, 파일이 없는 오류는 오류 코드입니다(예: `그룹 G1: 명세에 빠진 영향 대상: src/Web/OrdersController.cs#Cancel, src/App/Mapper.cs:12`). 검증할 수 없으면 파일을 고치기 전에 종료 코드 4(`GroupUnverifiable`)입니다: 그룹 파일이 속한 `.csproj`가 없음, 영향 범위를 확정할 수 없음(작업 공간 폴더가 2000개를 넘음, 목록이나 속성을 읽을 수 없는 폴더, 읽을 수 없는 솔루션·프로젝트 파일, 작업 공간 밖을 가리키거나 대상을 알 수 없는 링크·정션 폴더 — 작업 공간 안을 가리키는 링크는 건너뜁니다, `$(…)` 속성으로 경로를 만든 `ProjectReference`, 범위 안 프로젝트의 `ProjectReference`가 없는 `.csproj`를 가리킴), 레거시 프로젝트를 Windows 밖에서 빌드, 빌드 도구 없음, 기준 빌드의 비컴파일러 오류·시간 초과·시작 실패, 범위 안 다른 프로젝트가 참조하는 프로젝트에 기준 빌드부터 컴파일 오류가 있음(그 프로젝트를 참조하는 프로젝트는 컴파일되지 않아 깨져도 보이지 않습니다). 메시지는 `그룹 빌드 검증 불가 (<프로젝트>): <원인>` 꼴이며, 시간 초과는 `build.timeoutSeconds`의 초를, 비컴파일러 오류는 오류 코드·메시지와 조치를 적습니다. 편집 뒤의 검증 빌드가 검증할 수 없는 상태가 되면(시간 초과 등) 그룹을 되돌리고 종료 코드 4입니다. 그룹 승인은 빌드가 통과한 뒤 한 번 묻고(`--yes`·`--review-at-end`이면 묻지 않음) 거부하면 그룹을 되돌립니다(종료 코드 2). 성공하면 그룹의 모든 작업이 함께 `[x]`가 되고, 실패하면 그룹의 모든 작업에 `<!-- failed: <run-id> <종류> -->`가 붙습니다(종류: `GroupUnverifiable`, `GroupBuildFailed`, `GroupMissingTarget`, `GroupRejected` 또는 작업의 실패 종류). 되돌리는 중 pelly 밖에서 바뀐 파일은 덮어쓰지 않고 알리며(종료 코드 4), 그 파일의 작업은 `[x]`를 유지합니다(표시가 파일 상태와 맞도록). `--resume`은 `[x]`인 작업을 건너뛰고 첫 미완료 작업부터 이어 가므로, 실패한 그룹은 `[x]`가 아닌 작업만 한 그룹으로 다시 실행합니다. 그룹 일부가 `[x]`로 남았다면 그 작업들은 다시 실행되지 않으니 그 파일을 먼저 직접 확인하십시오. `spec check`와 `--resume`은 완료된 작업의 심볼·요청문 이름을 검사하지 않습니다(그룹이 이름을 바꿨을 수 있으므로). `--continue`는 실패한 그룹을 되돌린 뒤 다음 작업으로 가되, 그 그룹의 파일을 대상으로 하는 뒤 작업이 있으면 거기서 멈춥니다(검증할 수 없는 그룹이면 바로 멈춥니다). 첫 미완료 작업이 그룹 작업이면 `--dry-run`은 그 작업 하나만 그룹 모드(시그니처·이름 변경 허용)로 실행해 diff를 보여 줍니다. 영향 범위 탐색·기준 빌드·검증 빌드는 하지 않으므로 위의 종료 코드 4 사유도 검사하지 않고(dry-run이 통과해도 실제 apply가 검증 불가로 멈출 수 있습니다), 그룹의 나머지 작업은 실행하지 않으며, 파일과 명세 파일을 바꾸지 않습니다. 그룹 백업은 `.pelly/backup/<apply-id>.<그룹 ID>/`에 둡니다. 명세에 빠진 영향 대상은 위의 영향 탐색으로 미리 찾을 수 있습니다.
* **`--dry-run`:** 첫 미완료 작업 하나만 실행해 diff를 보여 주고, 파일과 명세 파일 어느 것도 쓰지 않습니다. 비대화형 실행에서도 `--yes` 없이 쓸 수 있습니다.
* **비대화형 실행에는 `--yes`가 필요합니다**(`--dry-run` 제외). 없으면 파일을 건드리기 전에 종료 코드 4입니다.
* **전체 작업이 성공하면** 명세를 `status: done`으로 바꿔 `changes/archive/<yyyy-MM-dd>-<id>.md`로 옮깁니다(같은 이름이 있으면 `-2`, `-3`). 하나라도 실패하면 보관하지 않고 `status: in-progress`로 둡니다.
* **기록:** 백업은 `.pelly/backup/<apply-id>/`, 요약은 `.pelly/runs/<apply-id>/apply.json`입니다. 작업별 실행 기록은 기존 `.pelly/runs/<run-id>/`입니다. apply 한 번이 작업 공간 잠금을 끝까지 잡습니다.
* **종료 코드:** 0 전부 성공, 1 검증 실패, 2 사용자 거부, 3 대상 찾기 실패, 4 환경·입력 오류(명세 오류, 실행 중 명세 파일을 바깥에서 고친 경우 포함), 5 안전 거부. `--continue`에서 전역적 오류(4)가 나면 거기서 멈춥니다.
* 명세 파일이 실행 중에 사람의 손으로 바뀌면 덮어쓰지 않고 종료 코드 4로 멈춥니다.

## `pelly rules` — 프로젝트 규칙 (AGENTS.md)

```
pelly rules init  [--dry-run] [--force] [--yes] [--pause-on-exit]
pelly rules check [--config <경로>] [--pause-on-exit]
```

작업 공간 루트의 `AGENTS.md`에 `## Pelly rules` 절을 두면 pelly가 규칙을 프롬프트에 넣고 `forbid` 규칙은 코드로 검사합니다. 하위 폴더의 `AGENTS.md`는 읽지 않습니다.

```
## Pelly rules
- [*] 새 주석은 꼭 필요할 때만 달고, 쓸 때는 한국어로 작성한다
- [async] .Result나 .Wait()로 기다리지 않는다 | forbid: \.Result\b|\.Wait\(\)
```

* 형식: `- [태그,...] 규칙(100자 이내) [| forbid: 정규식]`. `[*]`는 항상, 나머지는 태그가 이번 작업 코드에 나올 때만 들어갑니다. 규칙 예산(`budget.rules`)은 150 토큰이고, 넘으면 아래쪽 규칙부터 빠집니다.
* 업그레이드: 예전 `pelly.json`에 `budget.system`을 600으로 적어 두었다면 합계가 `budget.inputTotal`을 넘어 종료 코드 4가 됩니다. `budget.system`을 450으로 낮추십시오(규칙 150 토큰이 여기서 나뉨).
* `forbid` 규칙은 새로 추가된 줄의 코드 부분에서 검사합니다(주석과 문자열 리터럴은 빼고 검사). 어기면 고쳐 쓰게 재시도하고, 끝내 못 고치면 종료 코드 1입니다. 정규식 검사가 시간 안에 끝나지 않으면 종료 코드 4입니다.
* 규칙 형식 오류는 종료 코드 4입니다. `pelly rules check`로 원인을 봅니다.
* `pelly rules init`은 모델 없이 `.cs`·`.sql`을 분석해 초안을 보여 주고 승인을 받아 씁니다. 초안은 반드시 검토하십시오. 이미 절이 있으면 `--force` 없이는 종료 코드 4입니다. 거부하면 종료 코드 2, `--yes` 없는 비대화형 실행은 종료 코드 4입니다.
* `--verbose`는 포함·제외된 규칙을 보여 줍니다.

## 자주 쓰는 옵션

| 옵션 | 동작 |
| :--- | :--- |
| `--yes` | 승인 질문 없이 승인. 검증은 그대로 합니다 |
| `--dry-run` | 파일을 쓰지 않고 diff만 보여 줌 |
| `--test` | (`cs`만) 검증 때 테스트도 실행. 아래 절 참고 |
| `--verbose` | 끝난 뒤 단계별 시간·토큰·시도 요약과 실행 폴더 경로를 보여 줌. 프롬프트 원문은 출력하지 않고 `attempt-N/prompt.txt` 경로를 알려 줌 |
| `--pause-on-exit` | 결과를 보여 준 뒤 키 입력을 기다림. IDE 창이 바로 닫히지 않게 할 때 씀 |
| `--max-retries N` | 교정 재시도 최대 횟수 |
| `--strict-baseline` | (`cs`만) 원래 있던 CS 오류도 종료 코드 4로 처리 |
| `--warn-as-error` | (`cs`만) 새로 생긴 경고도 오류로 취급 |
| `--config <경로>` | 작업 공간 설정 파일 지정 |
| `--model`, `--endpoint`, `--think` | 설정을 이번 실행만 덮어쓰기 |

`--pause-on-exit`는 `pelly doctor`, `pelly report`, `pelly rules init`, `pelly rules check`에서도 똑같이 동작합니다.
모르는 옵션(예: `--dry-rum` 같은 오타)은 무시하지 않고 `Unknown option` 메시지와 함께 종료 코드 4로 멈춥니다. 아무것도 쓰지 않습니다. 값이 필요한 옵션을 값 없이 쓴 경우(예: `--out`을 줄 끝에 쓰거나 바로 뒤에 다른 옵션을 쓴 경우)도 `--out 옵션에 값이 없습니다`와 함께 종료 코드 4로 멈춥니다.
입력이 리디렉션되어 있으면 기다리지 않습니다. 키 대기는 Windows 미확인입니다.
SQL 안의 `#임시테이블` 컬럼, 동적 SQL 안의 문장, 다른 DB·연결된 서버 객체는 검증하지 못하고 알림만 보여 줍니다.

## `--test` — 테스트까지 검증 (C# 전용)

* 대상 프로젝트를 참조하고 이름에 `Test`가 든 테스트 프로젝트(또는 `test.projects`로 지정한 것)를 수정 전후로 실행합니다. **새로 실패한 테스트만** 실패로 봅니다.
* 원본 상태의 테스트를 돌리지 못하면 알림과 함께 빌드로만 검증합니다.
* 제한 시간은 `test.timeoutSeconds`(기본 300초)입니다.
* 레거시 프로젝트는 `test.command` 설정이 필요하며 Windows 미확인입니다.
* 테스트별 결과를 읽는 테스트 프로젝트: VSTest 방식(`Microsoft.NET.Test.Sdk`, 측정은 xunit v2), `xunit.v3`, `xunit.v3.mtp-*`. `MSTest.Sdk` 같은 다른 Testing Platform 프로젝트는 종료 코드만 봅니다.
* 여러 대상 프레임워크(`TargetFrameworks`)를 가진 Testing Platform 테스트 프로젝트(xunit v3, MSTest.Sdk 등)는 **첫 번째 TFM만** 실행합니다. 나머지 TFM에서만 깨지는 테스트는 잡지 못합니다.

## 종료 코드

| 코드 | 의미 |
| :-: | :--- |
| 0 | 성공 |
| 1 | 검증 실패 (재시도를 소진해도 통과하지 못함) |
| 2 | 사용자 거부 또는 중단 (Ctrl+C·종료 신호·콘솔 창 닫기, 원본 복원) |
| 3 | 대상 찾기 실패 |
| 4 | 환경 오류 (서버·모델·설정·잘못된 인자(모르는 옵션 포함)·외부 편집 충돌) |
| 5 | 안전 거부 |

중단 신호를 받으면 원본 복원이 끝날 때까지 최대 4.5초 기다린 뒤 끝납니다.
콘솔 창 닫기가 같은 경로를 타는지는 Windows 미확인입니다.

## 실행 기록

작업 공간의 `.pelly/runs/<runId>/`에 `summary.json`, `events.jsonl`, `attempt-*/`가 남습니다.
수정 전 원본은 `.pelly/backup/<runId>/`에 있습니다. `.pelly/`는 자동으로 버전 관리에서 제외됩니다.
오래된 기록은 `log.retentionDays`(기본 30일) 뒤에 지워집니다.

## 환경 점검: `pelly doctor`

```
pelly doctor [--project <csproj>]
```

* 서버, 모델, .NET SDK, SQL 연결과 함께 **의미 분석** 행을 보여 줍니다. pelly.exe 옆에 `BuildHost-net472`, `BuildHost-netcore` 폴더가 없으면 Fail(종료 코드 4)이니 반입 zip을 다시 풀어 두십시오.
* `--project`를 주면 그 프로젝트를 불러와 봅니다. 불러오지 못하면 Warn이고 종료 코드는 그대로이며, 영향 탐색은 이름 기준으로 대신합니다.

## 반출 자료 만들기: `pelly report --sanitize`

폐쇄망 밖으로 결과를 보내야 할 때 씁니다.

```
pelly report --sanitize --out C:\export
pelly report --since 2026-10-01 --sanitize
pelly doctor --sanitize --out C:\export
```

* `pelly-report-<시각>.json`과 `.md`가 만들어집니다. `--out`을 생략하면 현재 폴더에 씁니다.
* 허용 목록 방식입니다. 소스, 경로, 식별자, 모델 이름, RunId는 나가지 않고 숫자 지표, 오류 코드(`CS0103` 등), 실패 유형, 버전만 담깁니다.
* 반출 가능 여부는 사내 보안 규정을 따르십시오. 파일 내용을 직접 열어 확인한 뒤 반출하십시오.
* `--sanitize` 없이 `pelly report`만 실행하면 화면에 TTFT·성공률·완료 시간 표를 보여 줍니다.

## 알려진 한계

* **인라인 SQL은 DB로 검증하지 않습니다.** C# 문자열 안의 SQL(Dapper, `SqlCommand.CommandText`)은 C# 빌드만 통과하면 승인됩니다. 잘못된 컬럼명은 실행할 때 드러납니다. 바뀐 문자열 리터럴에 `SELECT`·`INSERT`·`UPDATE`·`DELETE`·`EXEC`가 있으면 「인라인 SQL이 바뀌었습니다」 경고를 `--yes`일 때도 보여 줍니다.
* **요청이 기존 테스트와 모순되면 `--test`가 속을 수 있습니다.** 모델이 요청을 로그 출력 같은 것으로 바꿔 회피하고 테스트를 통과해 종료 코드 0이 될 수 있습니다. 실측에서 20회 중 5회(25%, M3a), 같은 요청 단독 재측정에서 20회 중 7회(35%, 2026-10-19)였고, 현장과 같은 모델 파일(`qwen3.6:35b` GGUF)로는 20회 중 9회(45%, 2026-10-20)였습니다. `--test`를 써도 **diff를 직접 읽고** 승인하십시오.
* 레거시 .NET Framework 프로젝트의 빌드 검증과 `--test`는 Windows에서 실기 확인하지 않았습니다(Windows 미확인). 반입 후 현장에서 확인합니다(M4).
* SQL 경로(`pelly sql`)도 Windows에서 실기 확인하지 않았습니다(Windows 미확인).
* Razor 뷰(`.cshtml`)와 설정 파일(`web.config`, `appsettings.json`)은 수정 대상이 아닙니다.
* 컨트롤러 액션의 라우팅·필터 특성(`[HttpPost]`, `[Authorize]` 등)은 바꿀 수 없습니다. 의도된 동작입니다.
* 벤치마크는 현장과 같은 모델 파일(`qwen3.6:35b` GGUF `Q4_K_M`)과 0.9.1 기본값으로 50건 × 3회에서 평균 46.00건(92%) 성공이었습니다. 실패 4건 중 하나(`Math.Round(x, -1)`)는 빌드는 통과하고 실행할 때 예외가 나는 수정이라 빌드 검증으로는 잡히지 않습니다. 상세는 반입 패키지의 `benchmark-summary.md`에 있습니다. 이 수치는 개발 PC(Mac) 기준이고, Windows·GPU 서버 값은 현장에서 확인합니다.

## 관련 문서

* 설치: [install.md](install.md)
* IDE 연동: [ide-external-tools.md](ide-external-tools.md)
* 서버 관리자용 확인 사항: [server-checklist.md](server-checklist.md)
* 실행 중 외부 통신 없음: [no-network.md](no-network.md)
