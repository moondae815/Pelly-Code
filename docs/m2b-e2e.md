# M2b SQL 통합·실제 모델 E2E 실행 기록

* **날짜:** 2026-10-18
* **환경:** macOS arm64, .NET SDK 10.0.302, SQL Server 2022 컨테이너 `pelly-corpus-sql`(localhost,14333, `eval/corpus/Sql/deploy.sh`로 배포한 `PellyCorpus`), Ollama 0.40.0, 모델 `qwen3.6:35b-a3b-coding-nvfp4`, 프롬프트 `sql-v1`(바꾸지 않음), `llm.thinkMode = retry`(기본), `budget.think = 512`(기본)
* **대상:** `eval/corpus/Sql/objects`를 임시 폴더(`<임시>/objects`, 작업 공간 표시용 빈 `Sql.sln`)로 복사한 사본. 코퍼스 원본은 수정하지 않는다.
* **DB 불변 확인:** 모든 DB 테스트가 실행 전후에 사용자 객체 전부의 `OBJECT_DEFINITION`·`modify_date`와 객체 권한(`sys.database_permissions`)을 비교한다(`SqlE2E.WithUnchangedDatabase`). 한 번도 바뀌지 않았다.

## 1. 테스트

### 오프라인 통합 (`E2E/SqlOfflineEndToEndTests.cs`, `[SqlServerFact]` — CI `sql-integration`)

실제 SQL Server와 미리 정한 응답(`CannedLlmBackend`)으로 SQL 파이프라인(`FsmOrchestratorFactory.CreateSql`) 전체를 돌린다.
응답 수를 시도 수와 맞추려고 `llm.thinkMode = off`로 둔다(시도마다 생성 요청 1개).

| 테스트 | 응답 | 단언 |
| :--- | :--- | :--- |
| `CancelOrder_ValidEdit_Exit0` | `IF @Status = 3` 앞에 `@Status = 2` 가드 추가 | 종료 0 · 시도 1 · 대상 배치 밖 바이트 동일 · 파일에 `@Status = 2` · BOM·CRLF 유지 |
| `MissingColumn_ThenFix_Exit0_ErrorCodesHas207` | 위 응답에 `IF EXISTS (SELECT 1 FROM dbo.Orders o WHERE o.NoSuchColumn = 1) RETURN;` 삽입 → 그 줄을 지우는 응답 | 종료 0 · 시도 2 · `errorCodes ∋ Msg207` · 2번째 프롬프트에 `SQL CHECK FAILED`와 `NoSuchColumn` |
| `MissingTable_IsCaughtBy208_NotByCompile` | `IF EXISTS (SELECT 1 FROM dbo.NoSuchTable) RETURN;` 삽입 → 지우는 응답 | 같은 배치를 `CompileWithNoExecAsync`로 직접 컴파일하면 **오류 0개**(지연 이름 확인) · 실행은 종료 0 · 시도 2 · `errorCodes == [Msg208]` · 피드백에 `Invalid object name 'dbo.NoSuchTable'.` |
| `SettlementAll_EditsOnlyTargetBatch_GrantsUntouched` | `--object dbo.usp_SettleMonthly`, 열 하나 추가 | 종료 0 · 대상 배치 앞뒤 바이트 동일 · 두 `GRANT EXECUTE … GO` 그대로 |
| `ForbiddenEveryAttempt_Exit5_Restored` | 시도마다 `EXEC xp_cmdshell 'dir';` 추가(`--max-retries 2`) | 종료 5 · 시도 3 · 요청 3 · `failureKind = SqlSafetyRejected` · 2번째 프롬프트에 `SQL SAFETY REJECTED`와 `xp_cmdshell` · 파일 바이트 동일 |
| `HostNotAllowed_Exit5_NoGenerateCall` | `sql.allowedHosts = ["sql.example.invalid"]` | 종료 5 · 생성 요청 0 · `sql.allowedHosts에 없습니다` 안내 |
| `QuotedIdentifierOffFile_CompilesWithOff` | 사본의 `SET QUOTED_IDENTIFIER ON`을 `OFF`로 바꾸고 큰따옴표 문자열 `RAISERROR("…")` 추가 | 같은 배치를 직접 컴파일하면 QI ON에서 오류, OFF에서 0개 · 실행은 종료 0 · 시도 1 |
| `InjectStatement_SkipsPlacesThatBreakTheBatch` (`[Fact]`, 환경 불필요) | REPLACE 끝이 여러 줄 문장 중간인 응답 | 삽입기가 문장 경계를 찾아 넣고 대상 배치에 구문 오류가 없음 |

### 실제 모델 E2E (`E2E/SqlEndToEndTests.cs`, `[OllamaSqlFact]` — 로컬 전용)

`[OllamaSqlFact]`는 Ollama(`PELLY_TEST_OLLAMA`·`PELLY_TEST_MODEL`)와 SQL(`PELLY_TEST_SQL`)이 모두 있어야 돌고, Trait이 `Env=Ollama`라 CI에서 빠진다.

| 테스트 | 강제하는 것 | 단언 |
| :--- | :--- | :--- |
| `Sql001_RealModel_Exit0` | sql-001 요청 그대로 | 종료 0 · usp_CancelOrder 배치만 바뀜(앞뒤 바이트 동일) · `@Status = 2`(또는 `IN (…2…)`) 조건 아래 `RAISERROR`/`THROW` · 기존 Status 3 가드도 여전히 오류 |
| `FirstAttemptMissingColumn_FeedbackFixes` | `CorruptingBackend`: 첫 코드 응답의 첫 REPLACE에 `… o.NoSuchColumn = 1 …` 문장 삽입 | 종료 0 · 바꾼 응답 정확히 1개 · 시도 ≥ 2 · `errorCodes ∋ Msg207` · 2번째 프롬프트에 `SQL CHECK FAILED`와 `NoSuchColumn` · 요청 순서 `code → think → code` · 2번째 시도 `think.txt` · 최종 파일에 `NoSuchColumn` 없음 · sql-001 판정 |
| `MultiObjectFile_ObjectOption_Exit0` | sql-004(`Settlement_All.sql`, `--object dbo.usp_SettleMonthly`) | 종료 0 · 다른 배치(usp_SettleDaily·GRANT 둘) 바이트 동일 · 대상에 `SettlementDeferrals`와 `SUM(… Amount)` |
| `CliRun_DryRun_ShowsDiff` | 빌드한 `pelly sql`을 자식 프로세스로 실행(`--dry-run --yes`, `PELLY_SQL_CONNECTION`, 사본 루트의 `pelly.json` 허용 목록) | 종료 0 · stdout에 `@@ -a,b +c,d @@` hunk와 `@Status … 2` 추가 줄 · 파일 바이트 동일 |

삽입 자리(손상 방식)는 M2a R7과 같이 "첫 REPLACE 블록 마지막 줄 바로 앞"이 1안이다. 그 자리에 넣은 결과를 pelly의 `SqlSafetyGuard`가 받지 않으면
(여러 줄 문장 중간이라 구문 오류, 객체 밖 등) 블록 안의 앞쪽 자리를, 마지막으로 블록 끝 뒤를 차례로 시도한다. 손상 대상은 프롬프트가 `<<<<<<< SEARCH\n`으로 끝나는 첫 요청이다.

CLI 테스트는 테스트 프로젝트의 `ProjectReference`(`ReferenceOutputAssembly=false`, `Private=false`)로 테스트 빌드가 CLI를 먼저 빌드하게 해서 낡은 `pelly.dll`을 물지 않는다.

## 2. 최종 3회 실행

```bash
PELLY_TEST_SQL="Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=…;TrustServerCertificate=True" \
PELLY_TEST_OLLAMA=http://localhost:11434 PELLY_TEST_MODEL=qwen3.6:35b-a3b-coding-nvfp4 \
  dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*E2E*" --fail-skips on --minimum-expected-tests 1
```

세 번 모두 합계 23 · 실패 0 · 건너뜀 0 (53~55초). 23 = M2a E2E 8 + 삽입 검사 3(M2a) + SQL 오프라인 8 + SQL 실제 모델 4.

| 테스트 | 1회 | 2회 | 3회 | 통과 |
| :--- | :-: | :-: | :-: | :-: |
| `Sql001_RealModel_Exit0` | 통과 | 통과 | 통과 | 3/3 |
| `FirstAttemptMissingColumn_FeedbackFixes` | 통과 | 통과 | 통과 | 3/3 |
| `MultiObjectFile_ObjectOption_Exit0` | 통과 | 통과 | 통과 | 3/3 |
| `CliRun_DryRun_ShowsDiff` | 통과 | 통과 | 통과 | 3/3 |
| SQL 오프라인 8건 | 통과 | 통과 | 통과 | 3/3 |
| M2a E2E 8건 + 삽입 검사 3건 | 통과 | 통과 | 통과 | 3/3 |

최종 시리즈 전 같은 SQL 실제 모델 4건만 따로 1회(`--filter-class "*SqlEndToEndTests"`) 돌렸고 역시 4/4 통과했다. 실패는 한 번도 없었다 — 프롬프트(`sql-v1`)와 기대값은 바꾸지 않았다.

### 시도 수와 think

| 테스트 | 시도 | think 사용 | thinkTokens | think 잘림 | errorCodes | 매치 |
| :--- | :-: | :--- | -: | :-: | :--- | :--- |
| Sql001 (3회 모두) | 1 | 안 함 | 0 | — | — | `Exact` |
| FirstAttemptMissingColumn (3회 모두) | 2 | 2번째 시도만 | 512 | 예 | `Msg207` | `Exact` |
| MultiObjectFile (3회 모두) | 1 | 안 함 | 0 | — | — | `Indentation` |
| CliRun (3회 모두) | 1 | 안 함 | 0 | — | — | `Exact` |

* **강제 재시도는 3/3 실제로 일어났다**(데코레이터가 매번 정확히 1개 응답을 바꿨고 시도 2, `Msg207`). 2번째 시도에서 모델은 삽입된 줄만 지우는 블록을 냈다.
* THINK는 3/3 예산 512토큰에서 잘렸다(M2a와 같은 경향). 잘린 think로도 매번 올바르게 고쳤다.
* 세 번의 모델 출력(`output.txt`)은 테스트마다 바이트까지 같았다(코드 샘플링 temperature 0.1).
* MultiObjectFile의 모델 SEARCH 첫 줄은 들여쓰기가 빠진 `SELECT st.SellerId, …`라 `Indentation` 단계에서 맞았다.
  모델의 수정은 `SettlementDeferrals`를 판매자별로 합산해 LEFT JOIN하고 `ISNULL(def.TotalDeferral, 0) AS DeferralAmount` 열을 더했다. 이월 금액을 월(`@From`~`@To`)로 거르지는 않았다 — 요청문이 기간을 말하지 않아 판정에 넣지 않았다.

### 단계별 시간 (초, 최종 3회의 범위)

| 테스트 | Preflight | Locate | Slice | Edit | Apply | Verify¹ | TTFT² |
| :--- | -: | -: | -: | -: | -: | -: | -: |
| Sql001 | 0.09 | 0.00 | 0.01~0.02 | 6.1~6.2 | 0.01 | 0.02 | 4.5 |
| FirstAttemptMissingColumn | 0.09 | 0.00 | 0.02 | 18.6~19.2 | 0.00~0.01 | 0.03 | 6.9~7.2 |
| MultiObjectFile | 0.09 | 0.00 | 0.01 | 5.4~5.5 | 0.00~0.01 | 0.02 | 1.4~1.5 |
| CliRun (`--dry-run`) | 0.16~0.26 | 0.04~0.06 | 0.06~0.07 | 5.9~6.7 | 0.01 | —³ | 4.2~5.0 |

¹ Verify는 객체 존재 검사 + `SET NOEXEC ON` 컴파일이고, 첫 시도에서는 원본 배치의 기준 컴파일·존재 검사도 함께 한다(대상 배치 2회 컴파일 + `OBJECT_ID` 조회). 로컬 컨테이너에서 20~30ms다.
FirstAttemptMissingColumn의 0.03초는 실패한 1번째 + 통과한 2번째 검증의 합이다.

² **최종 시리즈의 Edit·TTFT는 M2a C# E2E와 같은 모델을 동시에 쓴 값이다**(xUnit이 테스트 클래스를 병렬로 돌린다). SQL 4건만 따로 돌린 사전 1회에서는
Sql001 Edit 1.71초 · TTFT 0.07초, MultiObjectFile Edit 4.33초 · TTFT 0.37초, FirstAttemptMissingColumn Edit 10.3초(두 시도 + THINK 512토큰) · TTFT 6.9초였다.
`CorruptingBackend`를 거치는 FirstAttemptMissingColumn의 TTFT는 데코레이터가 응답을 다 모은 뒤 흘려보내므로 전체 생성 시간에 가깝다(M2a와 같음).

³ `--dry-run`은 APPLY에서 diff를 보여 주고 끝나므로(`DryRunCompleted`) VERIFY(컴파일)를 하지 않는다. C# 경로와 같은 설계다.

## 3. CLI 직접 실행 1건 (`CliRun_DryRun_ShowsDiff`, 최종 1회)

```bash
PELLY_SQL_CONNECTION="Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=…;TrustServerCertificate=True" \
  dotnet src/PellyCoder.Cli/bin/Debug/net10.0/pelly.dll sql <사본>/objects/usp_CancelOrder.sql \
  "배송 중(Status = 2)인 주문도 취소할 수 없도록 오류를 발생시켜줘" --dry-run --yes \
  --model qwen3.6:35b-a3b-coding-nvfp4 --endpoint http://localhost:11434
```

`<사본>/pelly.json`: `{ "sql": { "allowedHosts": ["localhost"], "allowedDatabases": ["PellyCorpus"], "compatLevel": 160 } }` (비밀번호가 있는 연결은 D17에 따라 환경 변수로만 넘긴다).
종료 코드 **0**, 시도 **1**, think **안 함**, 매치 `Exact`, 입력 토큰 1016(로컬)/1023(서버)/1019(캐시). 세 번의 stdout(경로 줄 제외)은 바이트까지 같았다. 파일은 바뀌지 않았다.

```text
Preflight 사전 점검: usp_CancelOrder.sql
Locate 대상 찾기: 파일에서
Slice 문맥 구성
Edit (시도 1) 수정 생성 (시도 1/4)
Apply (시도 1) 패치 미리 보기
/var/folders/…/T/pelly-sql-e2e-db8984bfc98a4827a
26f1a98f4d7239e/objects/usp_CancelOrder.sql
```

```diff
@@ -31,6 +31,12 @@
         RETURN;
     END
 
+    IF @Status = 2
+    BEGIN
+        RAISERROR(N'배송 중인 주문은 취소할 수 없습니다.', 16, 1);
+        RETURN;
+    END
+
     IF @Status = 3
     BEGIN
         RAISERROR(N'배송 완료된 주문은 취소할 수 없습니다.', 16, 1);
```

(경로 줄이 둘로 갈린 것은 자식 프로세스의 콘솔 폭에서 Spectre가 줄바꿈한 것이다.)
