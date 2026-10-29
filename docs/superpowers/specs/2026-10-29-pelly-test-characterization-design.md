# `pelly test` — 현재 동작 고정 테스트(특성 테스트) 작성 설계

작성 2026-10-29. 상태: 설계, 사람 확인 전. 사람은 기능 진행을 결정했고(2026-10-29, 「레거시에 테스트가 작성되지 않은 것들이 많아」), 설계는 「질문 없이 바로 설계」를 골랐다. 「Claude 결정」으로 표시한 것은 사람이 아직 확인하지 않은 판단이다. 설계 검토(빌더 확인) 반영.

근거:
- PRD §12.4 「테스트 작성: v2에서는 사람이 작성」 — 이 기능이 그 줄을 바꾼다.
- 2026-10-28 `Report_AndSanitize` 조사(`docs/m4-field-model-baseline.md` 3절): 현장 모델이 빌드는 되지만 실행하면 예외가 나는 수정(`Math.Round(refund, -1, …)`)을 6/6 냈고, `--test`가 없으면 그대로 승인됐다. 테스트가 있어야 모델의 런타임 오류가 잡힌다.

## 1. 왜 하는가

레거시 솔루션에는 테스트가 없는 코드가 많다. 그러면 `pelly cs --test`와 `--verify-test`는 기댈 기준이 없고, 검증은 「빌드가 되는가」에서 멈춘다.

테스트가 없는 코드에 먼저 필요한 것은 「바뀔 동작」의 테스트가 아니라 **「지금 이렇게 동작한다」를 고정하는 테스트**다(특성 테스트, characterization test). 이 테스트가 있으면 이후 pelly의 수정(또는 사람의 수정)이 기존 동작을 깨는지 결정론적으로 잡힌다.

pelly의 원칙은 「모델이 쓰고, 코드가 판정한다」이다. 특성 테스트는 판정이 특히 쉽다:
- 지금 코드에서 **통과**해야 한다(정답은 현재 동작이다).
- 대상을 망가뜨리면 **실패**해야 한다(테스트가 대상을 실제로 검사한다).
- 두 번 돌려서 결과가 같아야 한다(비결정적이지 않다).

세 가지 모두 테스트를 실행해서 기계적으로 판정한다. 모델의 판단을 믿을 필요가 없다.

## 2. 목표 / 비목표 / 실패 조건

**목표**
1. `pelly test <FILE> --method <NAME>`이 C# 메서드 하나의 현재 동작을 고정하는 테스트 메서드 1~5개를 모델로 쓰고, 기존 테스트 프로젝트의 테스트 클래스에 넣는다.
2. 넣은 테스트를 판정한다: 형태 검사 → 빌드 → 현재 코드에서 통과 → 두 번째 실행에서도 같은 결과 → 대상 본문을 예외로 바꾼 변이에서 실패. 하나라도 어긋나면 피드백으로 재시도한다(`retry.max`).
3. 통과한 테스트의 diff를 보여 주고 사람이 승인하면 남긴다. 거부하거나 실패하면 테스트 파일(과 레거시 csproj)을 원래대로 되돌린다.
4. 대상 코드(운영 코드)는 실행 전후로 바이트가 같다.
5. `summary.json`·`pelly report`에 결과를 남긴다. PRD를 고친다.

**비목표**
- 테스트 프로젝트 만들기. 없으면 멈추고 안내한다(§4.2). 폐쇄망에서 테스트 패키지(MSTest·xUnit)를 구하는 일은 현장마다 달라서 pelly가 대신할 수 없다(Claude 결정).
- SQL(SP·함수) 테스트. tSQLt 같은 도구가 현장에 없다.
- 테스트하기 어렵게 짜인 코드를 고치기(의존성 주입 도입 등). 그런 대상은 실패로 끝나고 이유를 보여 준다.
- 커버리지 측정, 분기 전부 덮기. 개수는 1~5개다.
- `pelly ui`·`spec`·`apply` 연결. CLI 단독 명령만 만든다. 연결은 결과를 보고 나중에 정한다(Claude 결정).
- private 메서드를 직접 테스트하기. 테스트 프로젝트에서 부를 수 없는 대상은 멈춘다(§4.1).
- 기존 테스트 고치기. 새 메서드와 새 private 도우미만 더한다.

**실패 조건**
- 운영 코드 파일이 실행 뒤 바뀌어 있다(변이 복원 실패 포함).
- 판정 하나라도 건너뛴 테스트가 「성공」으로 남는다. 예: 테스트 결과를 개별로 읽을 수 없는 실행 방식에서 성공을 낸다.
- `Assert.True(true)`·단언 없는 테스트·대상을 부르지 않는 테스트가 통과한다.
- 거부·실패·취소 뒤 테스트 파일이나 csproj에 흔적이 남는다.
- 기존 테스트의 결과가 바뀐다(새 실패).
- 기존 `cs`·`sql`·`apply`·`spec`·`report`의 동작이 바뀐다. 예외: 보고서에 줄이 더해지는 것, 그리고 §4.5의 미완료 표시가 있을 때 `cs`·`sql`·`apply`가 멈추는 것(표시가 없으면 동작이 같다).

## 3. 사용 흐름

```text
pelly test Shop.Api/Services/OrderService.cs --method CalculateRefundAsync

대상: OrderService.CalculateRefundAsync(int)
테스트 프로젝트: Shop.Api.Tests (xUnit)
테스트 클래스: Shop.Api.Tests/OrderServiceTests.cs › OrderServiceTests (기존)
원본 상태 테스트 실행 중…
테스트 생성 (시도 1/4)
판정: 형태 ✓ · 빌드 ✓ · 현재 코드에서 통과 3/3 ✓ · 재실행 일치 ✓ · 변이 검출 2/3 ✗
  CalculateRefundAsync_PendingOrder_ReturnsZero: 대상이 예외를 던지게 바꿔도 통과함
테스트 생성 (시도 2/4)
판정: 형태 ✓ · 빌드 ✓ · 현재 코드에서 통과 3/3 ✓ · 재실행 일치 ✓ · 변이 검출 3/3 ✓

(테스트 파일 diff)

이 테스트 3개를 남길까요? [y/N]
```

- 인자: `<FILE>`(운영 코드 파일), `--method`(필수가 아님: `--line`이나 `--class`+`--method`도 `cs`와 같은 방식으로 받는다), `[HINT]`(선택, 200토큰 이내: 「취소 사유 길이 처리 위주로」처럼 무엇을 고정할지 힌트).
- 옵션: `--count <1-5>`(기본 3), `--test-project <PATH>`(후보가 여럿일 때), 그리고 `RunSettings`의 공통 옵션(`--yes`, `--dry-run`, `--verbose`, `--max-retries`, `--config`, `--think`, `--model`, `--endpoint`, `--pause-on-exit`).
- `--dry-run`: 판정까지 모두 하고(테스트 파일을 써야 빌드·실행할 수 있다) diff를 보여 준 뒤 되돌린다.
- 명령 이름 `test`는 Claude 결정이다. `cs --test`와 헷갈릴 수 있지만 도움말 첫 줄(「메서드의 현재 동작을 고정하는 테스트를 만듭니다」)로 구분한다. PowerShell 자동완성 목록에 더한다.

## 4. 설계

### 4.1 대상

- `cs`와 같은 `MemberLocator`로 메서드 하나를 찾는다. 메서드만 받는다(속성·생성자·이벤트는 멈춤, 종료 3).
- 접근성: `public`이거나, `internal`이고 운영 어셈블리에 테스트 어셈블리 이름을 가리키는 `InternalsVisibleTo`가 있을 때만. 아니면 「테스트 프로젝트에서 부를 수 없는 메서드입니다. 이 메서드를 부르는 public 메서드를 대상으로 하세요」(종료 3).
- 포함 타입이 `abstract`이거나 제네릭 타입이면 멈춘다(종료 3, 첫 판은 다루지 않음 — Claude 결정). 정적 클래스·정적 메서드는 받는다.
- 반복기(`yield`), `extern`·`partial` 선언만 있는 메서드, 본문 없는 메서드는 멈춘다(종료 3, 변이를 만들 수 없음 — Claude 결정).

### 4.2 테스트 프로젝트와 테스트 클래스

- 테스트 프로젝트: `TestProjectLocator.Find`(이미 있음: `test.projects` 설정 또는 이름에 Test가 들어가고 대상 프로젝트를 참조하는 프로젝트).
  - 0개: 「대상 프로젝트를 참조하는 테스트 프로젝트가 없습니다. 테스트 프로젝트를 만들고 참조를 추가하거나 `test.projects`를 설정하세요」(종료 3).
  - 2개 이상: `--test-project`가 없으면 후보를 보여 주고 멈춘다(종료 3).
- 실행 방식 제한: 테스트별 결과(TRX)를 읽을 수 있어야 한다. `TestProjectKind.OtherTestingPlatform`(종료 코드만)과 `{trx}` 없는 레거시 `test.command`는 「테스트별 결과를 읽을 수 없어 판정할 수 없습니다」(종료 4).
- 프레임워크: 테스트 프로젝트의 패키지 참조(SDK `PackageReference`, 레거시 `packages.config`)로 xUnit(v2·v3)·MSTest·NUnit을 정한다. 둘 이상이거나 없으면 종료 4. 프레임워크마다 정해진 표(테스트 특성 `[Fact]`/`[TestMethod]`/`[Test]`, 클래스 특성 `[TestClass]`/`[TestFixture]`, 단언 형식, 예외 단언 형식)를 쓴다.
- 테스트 클래스: 테스트 프로젝트의 C# 파일에서 이름이 `<대상 타입>Tests`인 클래스를 Roslyn으로 찾는다.
  - 하나: 그 클래스 끝에 넣는다. partial 클래스면 그 이름의 파일 이름이 `<타입>Tests.cs`인 부분을 쓴다.
  - 없음: `<테스트 프로젝트 폴더>/<타입>Tests.cs`를 새로 만든다. 네임스페이스는 테스트 프로젝트의 기존 C# 파일에서 가장 많이 쓴 네임스페이스, 없으면 `RootNamespace`, 없으면 어셈블리 이름. 클래스 뼈대(usings, 네임스페이스, 클래스 특성)는 코드가 프레임워크 표로 만든다. 레거시(비 SDK csproj)는 `<Compile Include="<타입>Tests.cs" />`를 기존 `Compile Include` 묶음 끝에 한 줄 더한다(문자열 삽입으로 원래 서식을 지킨다). 이 줄도 diff에 보인다.
  - 같은 이름의 파일은 있는데 클래스가 다르면 종료 3(덮어쓰지 않음).
- 변경 허용 범위: 테스트 파일 하나와(레거시 새 파일이면) csproj 하나. 그 밖의 파일은 쓰지 않는다.

### 4.3 모델 입력

새 프롬프트 종류 `cs-test`(`system.cs-test.txt`, `fewshot.cs-test.txt`). 예산은 기존 `budget`을 그대로 쓴다(입력 4,096, 출력 2,048, `num_ctx` 8,192).

CONTEXT 부분(모두 읽기 전용, 괄호는 우선순위 — 예산이 넘치면 큰 번호부터 뺀다. 대상 본문이 혼자 예산을 넘으면 멈춘다, 종료 3):
1. `[READ-ONLY: target]` 대상 메서드 원문 전체(문서 주석 포함)와 포함 타입 이름·네임스페이스. (빼지 않음)
2. `[READ-ONLY: construction]` 포함 타입의 생성자 서명과 필드(기존 슬라이서의 fields 부분과 같은 형식). (빼지 않음)
3. `[READ-ONLY: test helpers]` 테스트 클래스 안의 기존 private 도우미·가짜 객체(`FakeOrderRepository` 같은 중첩 클래스)의 서명. 가짜 객체는 멤버 서명과 생성자까지.
4. `[READ-ONLY: dependencies]` 생성자 매개변수와 대상이 부르는 필드의 타입 중 프로젝트에 선언된 인터페이스·추상 클래스의 멤버 서명(새 수집기, `DtoCollector`와 같은 탐색 한도).
5. `[READ-ONLY: dto]` 기존 `DtoCollector` 결과.
6. `[READ-ONLY: example test]` 테스트 클래스의 기존 테스트 메서드 중 가장 짧은 것 하나(문체 예시). 없으면 프레임워크 표의 예시.
7. `[READ-ONLY: test usings]` 테스트 파일의 using.

REQUEST는 코드가 정한 문장이다: 「위 대상의 현재 동작을 고정하는 테스트를 N개 써라. 현재 결과가 이상해 보여도 그대로 단언하라」 + 사람의 `[HINT]`.

출력 형식(Claude 결정): SEARCH/REPLACE가 아니라 두 구역.
```text
### USINGS
using Microsoft.Extensions.Logging.Abstractions;
### MEMBERS
[Fact]
public async Task CalculateRefundAsync_PendingOrder_ReturnsZero()
{
    ...
}
```
- `USINGS`는 없어도 된다. 기존 using과 겹치는 줄은 무시한다.
- `MEMBERS`는 C# 멤버 목록이다. 코드가 Roslyn으로 파싱해 클래스 끝(닫는 중괄호 앞)에 기존 들여쓰기로 넣는다.

### 4.4 판정 (순서대로, 앞이 실패하면 뒤는 하지 않는다)

| # | 판정 | 실패 시 피드백 |
| :- | :--- | :--- |
| 1 | **형태**: `MEMBERS`가 파싱된다. 테스트 메서드(프레임워크 테스트 특성이 있는 메서드)가 1~`--count`개다. 그 밖에는 private 메서드·private 중첩 클래스만 허용. 이름이 기존 멤버와 겹치지 않는다. 건너뛰기 특성(`Skip=`, `[Ignore]`, `Assert.Inconclusive`)이 없다. | 어긋난 항목 |
| 2 | **단언·호출**: 각 테스트가 대상 메서드 이름을 호출한다(구문 검사, 도우미를 거치면 같은 클래스의 private 도우미 한 단계까지 따라간다). 각 테스트에 단언이 하나 이상 있고, 단언 인자가 모두 리터럴인 단언(`Assert.True(true)`, `Assert.Equal(1, 1)`)만 있는 테스트는 거부한다. MSTest `[ExpectedException]`과 `Assert.Throws*`는 단언으로 센다. | 테스트 이름과 이유 |
| 3 | **환경 의존 금지**(Claude 결정): 새 테스트 코드에 `DateTime.Now`·`UtcNow`·`Today`, `Guid.NewGuid`, `new Random`, `Thread.Sleep`·`Task.Delay`, `System.IO.File`·`Directory`, `SqlConnection`, `HttpClient`, `Environment.`이 있으면 거부한다(테스트 코드만 검사, 대상 코드는 아님). | 「테스트가 시간·파일·DB·네트워크에 기대면 안 됨」 |
| 4 | **빌드**: 테스트 프로젝트 빌드. 새 오류가 테스트 파일에 있으면 피드백. 다른 파일의 새 오류는 환경 오류(종료 4). | 기존 빌드 피드백 형식 |
| 5 | **현재 코드에서 통과**: 테스트 프로젝트 전체 실행. 새 테스트는 모두 통과, 기존 테스트는 원본 상태(Baseline)와 비교해 새 실패 없음(`TestRegressions.Find`). | 실패한 새 테스트의 단언 메시지(기대·실제). 「현재 동작이 정답이다. 실제 값으로 단언을 고쳐라」 |
| 6 | **재실행 일치**: 한 번 더 실행해 새 테스트 결과가 같다. | 「비결정적인 테스트」 + 이름 |
| 7 | **변이 검출**: 대상 메서드 본문을 `throw new System.NotImplementedException("pelly-mutation");`(식 본문은 `=> throw …`, `async` 메서드는 CS1998 경고를 피하려고 `await System.Threading.Tasks.Task.Yield();`를 앞에 둔다)으로 바꾸고 테스트 프로젝트를 빌드·실행한다(`TestRunner`는 테스트 프로젝트만 빌드하고 운영 프로젝트는 참조로 함께 빌드된다). **새 테스트가 각각 실패**해야 한다. 그다음 대상 파일을 원래 바이트로 되돌리고 해시로 확인한다. | 통과해 버린 테스트 이름 + 「대상이 망가져도 통과함 — 대상의 반환값이나 효과를 단언하라」 |

- 판정 5의 실패에서 모델이 「실제 값」을 받아 단언을 고치는 것이 이 기능의 핵심 고리다. 기대값을 사람이 정하지 않으므로 RED가 없다.
- 판정 7은 「`Assert.ThrowsAny<Exception>`처럼 무엇이든 통과하는 테스트」와 「대상을 부르는 척만 하는 테스트」를 함께 잡는다. 변이 하나(본문 → 예외)만 쓴다. 여러 변이(조건 뒤집기 등)는 비목표다.
- 테스트 실행 횟수: 시도마다 1회(판정 5) + 마지막에 2회(판정 6·7). 원본 상태 실행 1회. 레거시 테스트가 느리면 시간이 길어진다 — `--verbose`에 단계별 시간을 보인다.
- 판정 6·7이 실패하면 재시도는 1번부터 다시 한다.
- **변이가 빌드되지 않으면**(분석기·경고를 오류로 처리 등) 판정 7은 「판정 불가」다. 통과로 치지 않는다: 테스트를 되돌리고 종료 4, 실패 유형 `CharTestMutationUnbuildable`, 빌드 오류 첫 줄을 보인다. 모델의 잘못이 아니므로 재시도하지 않는다(Claude 결정).
- **원본 상태 실행이 판정을 못 하면**(테스트 프로젝트 빌드 실패, TRX 없음, 실행 중단, 시간 초과) 모델을 부르기 전에 멈춘다(종료 4). 원본에서 이미 실패하는 기존 테스트는 괜찮다 — 비교는 새 실패만 본다(Claude 결정).

### 4.5 변이와 운영 코드 보호

변이는 사용자의 작업 사본에서 운영 파일을 잠깐 바꾼다(빌드가 실제 프로젝트를 써야 하므로). 테스트 파일(새로 만든 경우 포함)과 레거시 csproj도 판정 동안 바뀌어 있다. 보호 장치:
- 바꾸기 전에 기존 `FileGuard.Backup`으로 백업하고, 끝나면(성공·실패·예외·Ctrl+C) `finally`에서 원래 바이트로 쓰고 해시를 확인한다.
- 변이 중에 파일이 밖에서 바뀌었으면(해시가 변이 내용과 다름) 덮어쓰지 않고 종료 4와 백업 경로를 알린다(기존 `RestoreAsync` 규칙).
- 프로세스가 강제로 죽어 복원하지 못한 경우를 위해 **미완료 표시** `.pelly/runs/<runId>/chartest.pending`(JSON)을 둔다. 첫 파일 쓰기 직전에 만들고, 바꾼 파일마다(테스트 파일: 새로 만듦/백업 경로, csproj: 백업 경로, 운영 파일: 변이 중 여부·원본 해시·백업 경로) 기록을 갱신하며, 모든 파일을 남기거나 되돌린 뒤 지운다(Claude 결정).
- 표시를 확인하는 명령: `test`·`cs`·`sql`(Preflight)과 `apply`(시작할 때). 표시가 있으면 「이전 `pelly test` 실행이 끝나지 않았습니다: <파일 목록> — 백업 <경로>」를 보이고 종료 4로 멈춘다. 자동 복원하지 않는다(사람이 확인). `spec`·`report`·`doctor`·`ui`·`rules`·`completion`은 확인하지 않는다(파일을 쓰지 않거나 위 명령을 거친다 — Claude 결정). 사람이 확인한 뒤 표시 파일을 지우면 풀린다.
- 변이는 판정 1~6을 통과한 뒤 시도당 한 번만 한다.

### 4.6 결과와 기록

- 승인(또는 `--yes`): 테스트 파일(과 csproj)을 남긴다. 종료 0.
- 거부: 테스트 파일·csproj를 원래대로 되돌린다. 종료 2.
- 재시도를 다 써도 판정 실패: 되돌린다. 종료 1. 마지막 시도의 실패 판정과 피드백을 보여 준다.
- 운영 코드 해시가 실행 처음과 끝에 다르면 종료 4(실패 조건 — 일어나면 버그).
- `EditKind`에 `CSharpTest`를 더한다. `summary.json`에 `charTest` 블록: `requested`, `generated`, `kept`, `passedOnCurrent`, `stableOnRerun`, `killedByMutation`, `rejectedBy`(마지막 실패 판정 번호·이름), `framework`, `newFile`(bool). 새 실패 유형: `CharTestShape`, `CharTestNoAssert`, `CharTestEnvDependent`, `CharTestFailsOnCurrent`, `CharTestFlaky`, `CharTestSurvivesMutation`.
- `pelly report`: 「특성 테스트」 줄 — 실행 수, 성공률, 시도 평균, 실패 유형 분포. `--sanitize`는 개수와 유형만 남긴다(이름·경로 없음, 기존 규칙).

## 5. 바꾸는 곳 (짐작 — 계획에서 확정)

- CLI: `Commands/TestCommand.cs`(새), `CliApp.cs` 등록, PowerShell 자동완성 목록.
- Core: `CharTests/`(새 폴더): 테스트 클래스 찾기·만들기, 출력 파서, 판정 1~3, 변이, 프레임워크 표, 의존성 서명 수집기, 미완료 표시.
- **실행기는 따로 만든다**(Claude 결정). 기존 `FsmOrchestrator`·`RunContext`·`ApplyPhase`는 「대상 파일 = 고치는 파일」 하나를 백업·되돌리는 구조다(`FsmOrchestrator.cs:230-253`, `ApplyPhase.cs:33-67`). `test`는 고치는 파일이 대상과 다르고(새로 만들 수도 있고), csproj도 바뀌고, 대상 파일은 변이로 따로 쓰였다 되돌려진다. 그래서 공유 실행기를 여러 파일 되돌리기로 넓히지 않고(그러면 `cs`·`sql`·`apply`가 위험해진다) `CharTestOrchestrator`를 새로 둔다. 재사용은 부품 단위다: `MemberLocator`, `TestProjectLocator`, `TestRunner`, `TestRegressions`, `TrxParser`, `FileGuard`, `DtoCollector`, `OllamaBackend`와 생각 모드 처리, 빌드 피드백 형식, 검토(diff 승인) UI, `RunLogger`. 기존 `cs` 경로의 코드는 바꾸지 않는다(미완료 표시 확인 한 줄 예외).
- 프롬프트: `Llm/Resources/prompts/system.cs-test.txt`, `fewshot.cs-test.txt`.
- 기록: `RunLogger`·`ReportSanitizer`·`FailureKinds`.
- 문서: README `pelly test` 절, PRD(§12.4의 「테스트 작성」 줄, 새 절, 로드맵 행, 변경 이력), `deploy/usage.md` 짧은 안내, 현장 확인 항목(`deploy/field-results.md` 새 절).

## 6. 검증 (pelly 자체 테스트)

- 단위: 출력 파서, 판정 1~3(통과·거부 사례), 테스트 클래스 찾기·만들기(SDK·레거시 csproj 삽입), 프레임워크 판별, 변이 생성(블록 본문·식 본문·async), `mutation.pending` 처리, 실패 시 되돌림(가짜 LLM).
- 통합(가짜 LLM, 실제 빌드·테스트): Modern 코퍼스에서 정해진 응답으로 판정 4~7 각각의 통과·실패 사례. 특히 「대상이 망가져도 통과하는 테스트」가 판정 7에서 잡히는지, 운영 파일이 바이트 그대로인지.
- 레거시(MSTest, net48): Windows CI의 `windows-integration`에서 가짜 LLM으로 판정 4~7 한 사례.
- E2E(`[OllamaFact]`): Modern `CalculateRefundAsync`·`CancelAsync`에 실제 모델로 1회씩(연기 시험). 이 둘은 이미 테스트가 있으므로 새 테스트 이름이 기존과 겹치면 판정 1의 피드백으로 고쳐지는지도 본다. 측정(§7) 사례에는 넣지 않는다.

## 7. 측정 (예비)

- 사례: Modern·Legacy 코퍼스의 public 메서드 중 테스트가 없는 것 8개 정도(예: `SearchAsync`, Shop.Jobs·Shop.Data 쪽 메서드 — 계획에서 고른다)(`eval/benchmark/chartest/*.yaml`: `file`, `method`, `count`).
- 지표: 성공률(종료 0), 시도 수, 판정별 실패 분포, 변이 검출률, 시간.
- 로컬(Mac, 현장 모델)에서는 Modern만 잴 수 있다(레거시 net48 테스트 실행은 Windows). 결과는 「예비」로 표시하고 레거시는 현장 확인 항목으로 둔다.
- 하네스는 `pelly-eval`에 하위 명령을 더할지, 스크립트로 할지 계획에서 정한다.

## 8. 사람이 확인할 것 (현장)

- 현장 레거시 솔루션에 테스트 프로젝트가 있는가. 없다면 이 기능을 쓰기 전에 테스트 프로젝트(.NET Framework MSTest)를 만들 수 있는가(오프라인 NuGet).
- 레거시 `test.command`로 `vstest.console.exe … /logger:trx`를 쓸 수 있는가(TRX가 있어야 판정 가능).
- 변이가 작업 사본의 운영 파일을 잠깐 바꾸는 방식을 받아들일 수 있는가(IDE가 파일 변경을 감지해 다시 읽을 수 있다).

## 9. 빌더에게 맡기는 것

- 프롬프트 문구와 예시, 프레임워크 표의 세부(xUnit v2·v3 차이, NUnit 단언 형식).
- 의존성 서명 수집기의 탐색 방식(기존 `DtoCollector` 한도 재사용).
- 단계 표시 문구, `--verbose` 출력.
- 판정 2의 「호출」 추적 깊이를 넘는 경우의 처리(피드백 문구).
