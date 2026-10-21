# S6 작업 분해 프로브 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** qwen3.6:35b(현장 GGUF)가 다중 작업 요청을 V2-1 작업 줄로 분해하는 능력을, 사람이 승인한 정답 12건에 대해 기계 채점으로 측정하는 스파이크 도구를 만든다.

**Architecture:**
- `spikes/S6-planning/` 아래에 콘솔 프로젝트 하나와 테스트 프로젝트 하나를 둔다.
- 콘솔에는 하위 명령 다섯 개가 있다.
  - `index`: 코퍼스 색인
  - `run`: 모델 호출(arm A/B)
  - `callers`: 코드 비교군(arm C)
  - `score`: 채점
  - `report`: 집계와 판정
- 모든 응답 원문은 `raw/`에 저장한다. 채점은 원문만 읽으므로, 규칙을 고쳐도 모델을 다시 부르지 않는다.
- 제품 코드(src/)는 고치지 않고 `PellyCoder.Core`를 참조만 한다. 채팅 템플릿, 토큰 계수기, 디코딩을 그대로 쓴다.

**Tech Stack:**
- .NET 10 (SDK 10.0.302)
- Microsoft.CodeAnalysis.CSharp 5.9.0
- Microsoft.SqlServer.TransactSql.ScriptDom 170.191.0
- xunit.v3.mtp-v2 4.0.2
- Ollama `/api/generate` raw 모드

**Spec:** `docs/superpowers/specs/2026-10-21-s6-planning-probe-design.md`

## Global Constraints

- 측정 모델은 `qwen3.6:35b`이고, `/api/show` digest가 `a7eb95c53bcf`로 시작해야 한다. 아니면 `run`은 종료 코드 2로 거부한다.
- 입력 예산: 템플릿을 포함한 프롬프트가 `QwenTokenCounter.Shared.Count(...)` 기준 4096 토큰 이하여야 한다. num_ctx는 8192.
- 샘플링은 제품 기본값을 그대로 쓴다. `src/PellyCoder.Core/Config/PellyOptions.cs`의 `SamplingSet` 값이다.
  - Think: `(0.6, 0.95, 20, 1.0, 0.0)`
  - Code: `(0.1, 0.9, 20, 1.05, 0.0)`
- arm A: `EmptyThink`를 프리필하고 Code 샘플링, num_predict 1024.
- arm B:
  - 1단계: `ThinkOpen`을 프리필하고 Think 샘플링, num_predict 2048, stop은 `ThinkClose`.
  - 2단계: 1단계 출력과 `ThinkClose`를 이어 붙이고 Code 샘플링, num_predict 1024.
- 반복은 arm A·B 각각 사례당 5회. arm C는 범주 A만 1회.
- 작업 줄 문법: `- [ ] T<n> <cs|sql> <경로>[#심볼] <요청> [G<k>]`. 경로는 `eval/corpus/` 기준 상대 경로다.
- 실행 거부 조건(정답 승인 게이트): 다음 셋 중 하나라도 해당하면 `run`은 종료 코드 2로 거부한다. `callers`도 같은 게이트를 쓴다.
  - `cases/APPROVED.json`이 없다.
  - 승인 파일의 `goldCommit`이 `git log -1 --format=%H -- spikes/S6-planning/cases/*/gold.*`와 다르다.
  - `status: approved`가 아닌 `gold.md`가 하나라도 있다.
- 스파이크 프로젝트는 `spikes/Directory.Build.props`를 따르므로 TreatWarningsAsErrors가 false이고, 패키지 버전은 csproj에 직접 적는다. 그래도 경고 0을 목표로 한다.
- 커밋 트레일러: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
- `PELLY_TEST_*`, 비밀번호 등은 파일에 쓰지 않는다. 엔드포인트는 `PELLY_SPIKE_OLLAMA`, 모델은 `PELLY_SPIKE_MODEL` 환경 변수로 받는다.

## Review Focus

1. **모델이 코드 펜스(```)나 설명 문장으로 감싼 출력.** 펜스 줄과 산문 줄은 무시(`ExtraLines`로 셈)하고, 작업 줄만 파싱해야 한다. `- [ ]`로 시작하는데 문법이 틀린 줄만 `InvalidLines`로 센다. → Task 1 테스트
2. **경로·심볼 표기 변형.** 다음 둘만 정규화하고, 그 밖의 변형(`eval/corpus/` 접두 등)은 그대로 둔다. 그래서 지어낸 대상으로 센다. → Task 2 테스트
   - 경로: `\` → `/`, 앞의 `./` 제거
   - 심볼: `OrderService.CancelAsync`나 `CancelAsync()` → 마지막 점 뒤, 괄호 제거
3. **같은 대상을 두 번 낸 출력.** 재현율에서는 한 번으로 센다. 정밀도는 보고만 하고 판정에 쓰지 않는다. → Task 5 테스트
4. **arm B의 think가 2048 안에 닫히지 않은 경우.** `ThinkTruncated=true`를 기록하고, 2단계는 `ThinkClose`를 붙여 그대로 진행한다. → Task 7 테스트
5. **정답 파일의 내부 불일치.** `optional`·`order`·`groups`가 없는 T 번호를 가리키거나, 정답 대상이 색인에 없는 경우다. 로더와 색인 테스트가 실제 12건 전부를 검사한다. → Task 2·3 테스트

---

## File Structure

```
spikes/S6-planning/
  cases/<id>/gold.md, gold.constraints.json   # 이미 작성됨(초안 12건, status: draft)
  cases/APPROVED.json                          # 사람 승인 뒤 생성(Task 9)
  S6Planning/S6Planning.csproj
  S6Planning/PlanParser.cs       # 작업 줄 파서 (Task 1)
  S6Planning/Gold.cs             # TargetKey, GoldCase, GoldLoader (Task 2)
  S6Planning/CorpusIndex.cs      # 색인 생성·렌더·존재 확인 (Task 3)
  S6Planning/PlanPrompt.cs       # 프롬프트와 예산 (Task 4)
  S6Planning/Scorer.cs           # 반복 채점, Verdict (Task 5)
  S6Planning/CallerFinder.cs     # arm C (Task 6)
  S6Planning/Runner.cs           # 승인 게이트, digest, Ollama 호출, raw 저장 (Task 7)
  S6Planning/Report.cs           # results.csv, summary.md (Task 8)
  S6Planning/Program.cs          # 하위 명령 배선
  S6Planning.Tests/S6Planning.Tests.csproj, *Tests.cs, Fixtures/
  raw/<arm>/<case>/<n>.json      # 실행 산출물
  results/results.csv, results/summary.md
docs/s6-planning-probe.md        # 결과 보고서 (Task 9)
```

---

### Task 1: 프로젝트 골격과 작업 줄 파서

**Files:**
- Create: `spikes/S6-planning/S6Planning/S6Planning.csproj`(Exe, net10.0)
  - `ProjectReference`: `../../../src/PellyCoder.Core/PellyCoder.Core.csproj`
  - `PackageReference`: Microsoft.CodeAnalysis.CSharp 5.9.0, Microsoft.SqlServer.TransactSql.ScriptDom 170.191.0
- Create: `spikes/S6-planning/S6Planning.Tests/S6Planning.Tests.csproj`
  - `tests/PellyCoder.Eval.Tests/PellyCoder.Eval.Tests.csproj`와 같은 형태(OutputType Exe, `--ignore-exit-code 8`, `Using Xunit`)
  - xunit.v3.mtp-v2 4.0.2
- Create: `spikes/S6-planning/S6Planning/PlanParser.cs`, `Program.cs`(빈 하위 명령 분기)
- Create: `spikes/S6-planning/.gitignore`에 `raw/`, `results/`, `bin/`, `obj/`
- Test: `spikes/S6-planning/S6Planning.Tests/PlanParserTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record PlanTask(string Id, string Kind, string Path, string? Symbol, string Request, string? Group);`
  - `public sealed record ParsedPlan(IReadOnlyList<PlanTask> Tasks, IReadOnlyList<string> InvalidLines, int ExtraLines) { public bool Parsed => Tasks.Count > 0 && InvalidLines.Count == 0; }`
  - `public static class PlanParser { public static ParsedPlan Parse(string text); }`

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
[Fact] public void Parses_line_with_symbol_and_group()
{
    var p = PlanParser.Parse("- [ ] T1 cs Modern/Shop.Api/Services/IOrderService.cs#CancelAsync 반환 형식을 바꾼다 [G1]");
    var t = Assert.Single(p.Tasks);
    Assert.Equal(("T1","cs","Modern/Shop.Api/Services/IOrderService.cs","CancelAsync","반환 형식을 바꾼다","G1"),
                 (t.Id,t.Kind,t.Path,t.Symbol,t.Request,t.Group));
    Assert.True(p.Parsed);
}
[Fact] public void Sql_line_without_symbol_or_group()
{
    var t = Assert.Single(PlanParser.Parse("- [ ] T2 sql Sql/objects/usp_CancelOrder.sql 오류를 낸다").Tasks);
    Assert.Null(t.Symbol); Assert.Null(t.Group); Assert.Equal("오류를 낸다", t.Request);
}
[Fact] public void Fences_and_prose_are_extra_not_invalid()
{
    var p = PlanParser.Parse("Here is the plan:\n```\n- [ ] T1 cs A.cs#M 고친다\n```\n");
    Assert.Single(p.Tasks); Assert.Empty(p.InvalidLines); Assert.Equal(3, p.ExtraLines);
}
[Fact] public void Checkbox_line_with_bad_kind_is_invalid()
{
    var p = PlanParser.Parse("- [ ] T1 java A.java#M 고친다\n- [ ] T2 cs A.cs#M 고친다");
    Assert.Single(p.InvalidLines); Assert.False(p.Parsed);
}
[Fact] public void Missing_request_is_invalid() =>
    Assert.Single(PlanParser.Parse("- [ ] T1 cs A.cs#M").InvalidLines);
[Fact] public void Empty_output_is_not_parsed() => Assert.False(PlanParser.Parse("").Parsed);
```

- [ ] **Step 2: 실패 확인**

Run: `cd spikes/S6-planning/S6Planning.Tests && dotnet test`
Expected: 컴파일 실패(`PlanParser` 없음)

- [ ] **Step 3: `PlanParser.Parse` 구현**
  - 줄 단위(`\r\n`, `\n`)로 처리한다.
  - `- [`로 시작하는 줄은 정규식 `^- \[ \] (T\d+) (cs|sql) (\S+?)(?:#(\S+))? (.+?)(?: \[(G\d+)\])?\s*$`에 맞아야 하고, 맞지 않으면 Invalid로 센다. 경로와 심볼은 첫 공백 전 토큰을 `#`로 나눈다.
  - 빈 줄은 무시한다.
  - 그 밖의 줄은 ExtraLines로 센다.

- [ ] **Step 4: 통과 확인** — `dotnet test` → 6 passed

- [ ] **Step 5: 커밋** — `git add spikes/S6-planning && git commit -m "spike(S6): 작업 줄 파서"`

---

### Task 2: 대상 키와 정답 로더

**Files:**
- Create: `spikes/S6-planning/S6Planning/Gold.cs`
- Test: `spikes/S6-planning/S6Planning.Tests/GoldTests.cs`

**Interfaces:**
- Consumes: `PlanTask`, `PlanParser.Parse` (Task 1)
- Produces:
  - `public static class TargetKey { public static string Of(string kind, string path, string? symbol); public static string Of(PlanTask t); }`
    - cs: `"<경로>#<멤버>"`, sql: `"<경로>"`
    - 경로는 `\`→`/`, 앞 `./` 제거, 소문자화
    - 멤버는 마지막 `.` 뒤, 끝의 `()`·`(...)` 제거, 대소문자 유지
  - `public sealed record GoldCase(string Id, char Category, IReadOnlyList<string> Scope, string Status, string Request, IReadOnlyList<PlanTask> Tasks, IReadOnlySet<string> Optional, IReadOnlyList<(string Before, string After)> Order, IReadOnlyList<IReadOnlyList<string>> Groups, IReadOnlyList<string> Forbidden, string Dir);`
    - `Forbidden`은 `"<경로>#<멤버>"` 원문이다. `TargetKey.Of("cs", 경로, 멤버)`로 키를 만든다.
    - `public IEnumerable<PlanTask> Required => Tasks.Where(t => !Optional.Contains(t.Id));`
  - `public static class GoldLoader { public static GoldCase Load(string caseDir); public static IReadOnlyList<GoldCase> LoadAll(string casesDir); }`
    - 파일 머리말(`---` 사이)은 `id`, `category`, `scope`(`[A, B]` 형태), `status`, `request`(큰따옴표, `\"` 이스케이프)를 줄 단위로 파싱한다. YAML 라이브러리는 쓰지 않는다.
    - `optional`·`order`·`groups`가 없는 T 번호를 가리키면 `InvalidDataException`을 던진다.

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
[Theory]
[InlineData("cs", @".\Modern\A.cs", "OrderService.CancelAsync()", "modern/a.cs#CancelAsync")]
[InlineData("cs", "Modern/A.cs", "CancelAsync", "modern/a.cs#CancelAsync")]
[InlineData("sql", "Sql/objects/x.sql", "dbo.usp_X", "sql/objects/x.sql")]
[InlineData("cs", "eval/corpus/Modern/A.cs", "M", "eval/corpus/modern/a.cs#M")]   // 접두는 정규화하지 않는다
public void TargetKey_normalizes_only_agreed_variants(string k, string p, string s, string expected) =>
    Assert.Equal(expected, TargetKey.Of(k, p, s));

[Fact] public void Loads_all_twelve_drafts_consistently()
{
    var all = GoldLoader.LoadAll(TestPaths.Cases);                       // spikes/S6-planning/cases
    Assert.Equal(12, all.Count);
    Assert.Equal(new[]{'A','B','C','D'}, all.Select(c => c.Category).Distinct().Order());
    Assert.All(all, c => Assert.Equal(3, all.Count(x => x.Category == c.Category)));
    Assert.All(all, c => Assert.NotEmpty(c.Required));
    Assert.All(all, c => Assert.DoesNotContain("#", c.Request));        // 요청에 파일·심볼 없음
}
[Fact] public void Constraint_pointing_to_missing_task_throws() =>
    Assert.Throws<InvalidDataException>(() => GoldLoader.Load(TestPaths.Fixture("bad-order-ref")));
```

`TestPaths`는 리포 루트를 `global.json`으로 찾는다. 픽스처 `Fixtures/bad-order-ref/`는 작업이 T1뿐인데 `order: [["T1","T9"]]`인 정답이다.

- [ ] **Step 2: 실패 확인** — `dotnet test` → 컴파일 실패
- [ ] **Step 3: `TargetKey`, `GoldCase`, `GoldLoader` 구현** (위 Interfaces 그대로)
- [ ] **Step 4: 통과 확인** — `dotnet test` → 전부 통과
- [ ] **Step 5: 커밋** — `git commit -m "spike(S6): 대상 키·정답 로더"`

---

### Task 3: 코퍼스 색인

**Files:**
- Create: `spikes/S6-planning/S6Planning/CorpusIndex.cs`
- Test: `spikes/S6-planning/S6Planning.Tests/CorpusIndexTests.cs`

**Interfaces:**
- Consumes: `TargetKey`, `GoldLoader` (Task 2)
- Produces:
  - `public sealed record IndexMember(string TypeName, string Name, string Signature);`
  - `public sealed record IndexFile(string Path, string Kind, IReadOnlyList<IndexMember> Members);`
  - `public sealed record CorpusIndex(IReadOnlyList<IndexFile> Files) { public string Render(); public bool Exists(PlanTask t); public bool Exists(string kind, string path, string? symbol); }`
  - `public static class CorpusIndexBuilder { public static CorpusIndex Build(string corpusRoot, IReadOnlyList<string> scope); }`

**규칙:**
- **대상 범위**
  - scope `Modern`과 `Legacy`는 해당 폴더 아래의 `*.cs`이고, `obj/`, `bin/`, `TestResults/`, `AssemblyInfo.cs`는 뺀다.
  - scope `Sql`은 `Sql/objects/*.sql`만이다(schema는 뺀다).
  - 파일 순서는 경로의 서수(ordinal) 정렬을 따른다.
- **C# 파일**
  - `TextDecoding.Decode(bytes)`로 읽어 `CSharpSyntaxTree.ParseText`로 구문 트리를 만든다.
  - 모든 형식(중첩 포함, 이름은 `Outer.Inner`)의 메서드와 속성을 넣는다. 인터페이스 멤버도 넣는다. 생성자와 필드는 넣지 않는다.
  - Signature는 수정자, 반환 형식, 이름, 매개변수 목록으로 쓴다. 본문, 특성, 주석은 넣지 않는다.
- **SQL 파일**
  - ScriptDom `TSql170Parser`로 파싱한다.
  - `CREATE PROCEDURE`, `FUNCTION`, `VIEW`마다 `IndexMember("", "dbo.<이름>", "PROCEDURE (@p type, ...)")`를 하나씩 만든다.
- **Render**
  - 파일마다 헤더 `## <경로> (<kind>)`를 쓴다.
  - 멤버마다 `- <TypeName>.<Name>: <Signature>` 한 줄을 쓴다. SQL은 `- <Name>: <Signature>`로 쓴다.
- **Exists**
  - 파일(키 비교)과 kind가 맞아야 한다.
  - cs는 심볼이 필수이고, 마지막 세그먼트가 그 파일의 어떤 멤버 `Name`과 같아야 한다.
  - sql은 심볼이 없어도 되고, 있으면 `dbo.` 유무와 무관하게 객체 이름과 같아야 한다.

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
[Fact] public void Modern_index_includes_nested_test_fake_member()
{
    var idx = CorpusIndexBuilder.Build(TestPaths.Corpus, new[]{"Modern"});
    var f = idx.Files.Single(x => x.Path == "Modern/Shop.Api.Tests/OrderServiceTests.cs");
    Assert.Contains(f.Members, m => m.TypeName == "OrderServiceTests.FakeOrderRepository" && m.Name == "UpdateStatusAsync");
}
[Fact] public void Multi_object_sql_file_lists_each_object()
{
    var f = CorpusIndexBuilder.Build(TestPaths.Corpus, new[]{"Sql"}).Files.Single(x => x.Path == "Sql/objects/Settlement_All.sql");
    Assert.Contains(f.Members, m => m.Name == "dbo.usp_SettleDaily");
    Assert.Contains(f.Members, m => m.Name == "dbo.usp_SettleMonthly");
}
[Fact] public void Render_has_no_method_bodies() =>
    Assert.DoesNotContain("SqlConnection(", CorpusIndexBuilder.Build(TestPaths.Corpus, new[]{"Modern"}).Render());
[Fact] public void Every_gold_target_and_forbidden_exists_in_its_scope_index()
{
    foreach (var c in GoldLoader.LoadAll(TestPaths.Cases))
    {
        var idx = CorpusIndexBuilder.Build(TestPaths.Corpus, c.Scope);
        Assert.All(c.Tasks, t => Assert.True(idx.Exists(t), $"{c.Id} {t.Id}"));
        Assert.All(c.Forbidden, f => { var (p, _, s) = f.Partition('#'); Assert.True(idx.Exists("cs", p, s), f); });
    }
}
[Fact] public void Exists_rejects_unknown_member_and_wrong_kind()
{
    var idx = CorpusIndexBuilder.Build(TestPaths.Corpus, new[]{"Modern"});
    Assert.False(idx.Exists("cs", "Modern/Shop.Api/Services/OrderService.cs", "RefundAsync"));
    Assert.False(idx.Exists("sql", "Modern/Shop.Api/Services/OrderService.cs", null));
    Assert.False(idx.Exists("cs", "Modern/Shop.Api/Services/OrderService.cs", null));
}
```

`Partition`은 테스트 안의 작은 확장 메서드다.

- [ ] **Step 2: 실패 확인** — `dotnet test` → 컴파일 실패
- [ ] **Step 3: `CorpusIndexBuilder.Build`, `Render`, `Exists` 구현** (위 규칙 그대로)
- [ ] **Step 4: 통과 확인** — `dotnet test` → 전부 통과
- [ ] **Step 5: `index` 하위 명령 배선**
  - 명령: `dotnet run --project S6Planning -- index <caseId>`
  - 동작: 그 사례의 Render를 표준 출력으로 내고, 마지막 줄에 `tokens=<n>`을 쓴다. 이 명령을 커밋에 포함한다.
- [ ] **Step 6: 커밋** — `git commit -m "spike(S6): 코퍼스 색인"`

---

### Task 4: 프롬프트와 입력 예산

**Files:**
- Create: `spikes/S6-planning/S6Planning/PlanPrompt.cs`
- Test: `spikes/S6-planning/S6Planning.Tests/PlanPromptTests.cs`

**Interfaces:**
- Consumes: `GoldCase` (Task 2), `CorpusIndex` (Task 3), `QwenChatTemplate.LoadDefault()`, `QwenTokenCounter.Shared`
- Produces:
  - `public static class PlanPrompt { public const string System = "..."; public static readonly string[] ExampleNames; public static string User(string request, CorpusIndex index); public static string Raw(QwenChatTemplate t, string request, CorpusIndex index, bool think); public static int CountTokens(string raw); }`
    - `Raw`: 템플릿의 `Build(System, User(...))`에 `think`이면 `ThinkOpen`, 아니면 `EmptyThink`를 붙인다.
    - `CountTokens`는 `QwenTokenCounter.Shared.Count`를 위임한다.

**System 프롬프트(영어, 고정):** 실행자는 아래 내용을 문장으로 옮긴다. 실행 중에는 바꾸지 않는다.
1. You split a change request into tasks for a tool that edits exactly one C# member or one SQL file per task.
2. Output only task lines in this grammar, one per line: `- [ ] T<n> <cs|sql> <path>[#Member] <what to change> [G<k>]`.
3. Use only paths and members that appear in the index. Paths are copied exactly.
4. If a member's signature changes, put that task and every task needed for the code to build again in the same group `[G1]`.
5. List a changed declaration before the code that uses it.
6. Do not add tasks the request does not need.

예시 하나는 도서 대출 도메인으로 쓴다. 파일은 `Library/LoanService.cs#Extend`, `Library/ILoanService.cs#Extend`, `Db/usp_ExtendLoan.sql`이다. `ExampleNames`는 `{"LoanService", "ILoanService", "usp_ExtendLoan", "Library/"}`이다.

**User:** `Index:\n<Render>\n\nRequest:\n<요청>`

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
[Fact] public void All_twelve_cases_fit_input_budget()
{
    var t = QwenChatTemplate.LoadDefault();
    foreach (var c in GoldLoader.LoadAll(TestPaths.Cases))
    {
        var idx = CorpusIndexBuilder.Build(TestPaths.Corpus, c.Scope);
        Assert.True(PlanPrompt.CountTokens(PlanPrompt.Raw(t, c.Request, idx, think: true)) <= 4096, c.Id);
    }
}
[Fact] public void Example_names_do_not_collide_with_corpus()
{
    var all = CorpusIndexBuilder.Build(TestPaths.Corpus, new[]{"Modern","Legacy","Sql"}).Render();
    Assert.All(PlanPrompt.ExampleNames, n => Assert.DoesNotContain(n, all));
}
[Fact] public void Raw_prefill_differs_by_arm()
{
    var t = QwenChatTemplate.LoadDefault(); var idx = new CorpusIndex(Array.Empty<IndexFile>());
    Assert.EndsWith(t.EmptyThink, PlanPrompt.Raw(t, "r", idx, think: false));
    Assert.EndsWith(t.ThinkOpen, PlanPrompt.Raw(t, "r", idx, think: true));
}
```

- [ ] **Step 2: 실패 확인** — `dotnet test` → 컴파일 실패
- [ ] **Step 3: `PlanPrompt` 구현**
  - 예산을 넘는 사례가 있으면 Step 4에서 실패한다. 이때는 설계 §5에 따라 정답 사례를 바꿔야 하며, 색인을 자르지 않는다. 이 경우 작업을 멈추고 보고한다(BLOCKED).
- [ ] **Step 4: 통과 확인** — `dotnet test` → 전부 통과. `index <caseId>`의 토큰 수 12건을 커밋 메시지 본문에 적는다.
- [ ] **Step 5: 커밋** — `git commit -m "spike(S6): 프롬프트·입력 예산"`

---

### Task 5: 채점기와 판정

**Files:**
- Create: `spikes/S6-planning/S6Planning/Scorer.cs`
- Test: `spikes/S6-planning/S6Planning.Tests/ScorerTests.cs`, `spikes/S6-planning/S6Planning.Tests/Fx.cs`

**Interfaces:**
- Consumes: `PlanParser`, `TargetKey`, `GoldCase`, `CorpusIndex`, `PlanPrompt.ExampleNames`
- Produces:
  - `public sealed record RunScore(string CaseId, char Category, string Arm, int Rep, bool Parsed, double Recall, double Precision, int Hallucinated, int Forbidden, int OrderViolations, bool GroupOk, bool KindOk, int Outside, bool ExampleCopy, bool Usable);`
  - `public static class Scorer { public static RunScore Score(GoldCase gold, CorpusIndex index, string arm, int rep, string output); }`
  - `public sealed record CategoryRate(char Category, int Usable, int Total);`
  - `public enum VerdictKind { PremiseRejected, Partial, PremiseConfirmed }`
  - `public static class Verdict { public static VerdictKind Decide(IReadOnlyList<RunScore> armScores); }`

**정의** (설계 §6.2 그대로):
- **기본 값**
  - `Recall`: 출력 대상 키 집합 ∩ 필수 키 집합 / 필수 키 수. 같은 키가 여러 번 나와도 한 번으로 센다.
  - `Precision`: (필수 ∪ 선택)과 겹친 출력 키 수 / 출력 키 수. 출력이 0개이면 0.
  - `Hallucinated`: `!index.Exists(task)`인 작업 수.
  - `Forbidden`: 출력 키 중 gold.Forbidden 키의 수.
  - `ExampleCopy`: 출력 원문에 `ExampleNames` 중 하나가 들어 있다.
  - `KindOk`: 정답과 키가 같은 작업은 모두 kind도 같다.
- **조건부 값**
  - `OrderViolations`: gold.Order의 각 (앞, 뒤) 쌍에서, 두 대상이 모두 출력에 있고 뒤 대상의 첫 등장 위치가 앞 대상의 첫 등장 위치보다 앞이면 1로 센다.
  - `GroupOk`: 범주 A에서 gold.Groups의 각 그룹에 대해, 출력에 있는 해당 대상들의 Group이 모두 같고 null이 아니다. 범주 A가 아니면 true.
  - `Outside`: 출력 키 중 필수 ∪ 선택에 없는 키의 수(중복 제거).
- **종합**
  - `Usable`은 다음을 모두 만족할 때 true다.
    - `Parsed`, `Recall == 1`
    - `Hallucinated == 0`, `Forbidden == 0`, `OrderViolations == 0`
    - `GroupOk`, `!ExampleCopy`
    - (범주 D) `Outside == 0`
- **Verdict.Decide** (한 arm의 60반복). r = Usable / 전체, h = Hallucinated > 0인 반복 / 전체.
  - `PremiseRejected`: r ≥ 0.8이고, 모든 범주에서 Usable 비율 ≥ 0.6이고, 전체 Hallucinated 합이 0.
  - `PremiseConfirmed`: r < 0.5이거나 h > 0.1.
  - `Partial`: 그 밖.

- [ ] **Step 1: 실패하는 테스트 작성**

인메모리 `GoldCase`와 작은 `CorpusIndex`, 균등 분배된 점수 목록을 만드는 헬퍼 `Fx.Gold(...)`, `Fx.Index(...)`, `Fx.Scores(...)`를 `S6Planning.Tests/Fx.cs`에 둔다. Task 8도 이 헬퍼를 쓴다.

```csharp
[Fact] public void Duplicate_targets_count_once_for_recall() { /* 필수 2개, 출력은 T1 같은 대상 2번 + T2 → Recall 1, Precision 1 */ }
[Fact] public void Optional_missing_is_still_usable() { /* 선택 T3 없음 → Usable */ }
[Fact] public void Unknown_member_is_hallucinated_and_not_usable() { }
[Fact] public void Forbidden_target_blocks_usable() { }
[Fact] public void Caller_before_interface_is_order_violation() { /* order [T1,T2], 출력 T2가 먼저 → OrderViolations 1 */ }
[Fact] public void Category_A_requires_single_group() { /* 필수 둘 중 하나에 [G1], 하나는 그룹 없음 → GroupOk false */ }
[Fact] public void Category_D_extra_task_is_outside_and_not_usable() { }
[Fact] public void Example_name_in_output_blocks_usable() { /* 출력에 "LoanService" */ }
[Theory]
[InlineData(48, 60, 0, 0, VerdictKind.PremiseRejected)]   // 정확히 80%, 범주별 12/15
[InlineData(48, 60, 1, 0, VerdictKind.Partial)]           // 지어낸 대상 1건
[InlineData(29, 60, 0, 0, VerdictKind.PremiseConfirmed)]  // 48.3%
[InlineData(40, 60, 7, 7, VerdictKind.PremiseConfirmed)]  // 지어낸 반복 11.7%
public void Verdict_boundaries(int usable, int total, int hallucTotal, int hallucReps, VerdictKind expected) { /* Fx.Scores(...) 로 균등 분배 */ }
[Fact] public void Verdict_partial_when_one_category_below_60() { /* 전체 50/60, 범주 D 8/15 → Partial */ }
```

각 테스트 본문은 주석에 적은 입력으로 `Scorer.Score`나 `Verdict.Decide`를 부르고, 주석에 적은 값을 단언한다.

- [ ] **Step 2: 실패 확인** — `dotnet test` → 컴파일 실패
- [ ] **Step 3: `Scorer.Score`, `Verdict.Decide` 구현** (위 정의 그대로)
- [ ] **Step 4: 통과 확인** — `dotnet test` → 전부 통과
- [ ] **Step 5: 커밋** — `git commit -m "spike(S6): 채점기·판정"`

---

### Task 6: arm C — 구문 기반 호출부 찾기

**Files:**
- Create: `spikes/S6-planning/S6Planning/CallerFinder.cs`
- Test: `spikes/S6-planning/S6Planning.Tests/CallerFinderTests.cs`, 픽스처 `Fixtures/mini-corpus/Mini/` (cs 파일 넷)

**Interfaces:**
- Consumes: `CorpusIndexBuilder`(파일 목록), `PlanTask`, `GoldCase`
- Produces: `public static class CallerFinder { public static string Plan(string corpusRoot, IReadOnlyList<string> scope, PlanTask seed); }`
  - 반환값은 작업 줄 텍스트다. 채점기가 모델 출력과 똑같이 읽는다.

**알고리즘:**
- 씨앗 멤버의 이름을 N, 그 멤버를 선언한 형식의 이름을 S라 한다.
  1. 씨앗 자체를 T1으로 둔다.
  2. 구현체: 기본 형식 목록에 S가 이름으로 들어 있는 형식(중첩 포함) 안의 멤버 N.
  3. 호출부: 본문에 `InvocationExpressionSyntax`가 있고, 그 대상이 `MemberAccess.Name == N`인 메서드나 속성. 1~2에서 이미 넣은 멤버는 뺀다.
- 순서는 씨앗, 구현체(경로 순), 호출부(경로 순)이다.
- 모두 `[G1]`, 요청 문구는 `(code)`로 둔다.
- 이름만으로 맞추므로 다른 형식의 같은 이름 메서드도 잡는다. 이것은 의도된 한계이고 보고서에 적는다.

- [ ] **Step 1: 실패하는 테스트 작성**

미니 코퍼스:
- `IRepo.cs`: `interface IRepo { int Save(int x); }`
- `Repo.cs`: `class Repo : IRepo { public int Save(int x) => x; }`
- `Svc.cs`: `class Svc { IRepo r; public void Run() { r.Save(1); } public void Idle() {} }`
- `Tests.cs`: `class T { sealed class Fake : IRepo { public int Save(int x) => 0; } }`

```csharp
[Fact] public void Finds_seed_implementers_including_nested_fake_then_callers()
{
    var text = CallerFinder.Plan(TestPaths.Fixture("mini-corpus"), new[]{"Mini"},
        new PlanTask("T1","cs","Mini/IRepo.cs","Save","(seed)",null));
    var keys = PlanParser.Parse(text).Tasks.Select(TargetKey.Of).ToArray();
    Assert.Equal(new[]{"mini/irepo.cs#Save","mini/repo.cs#Save","mini/tests.cs#Save","mini/svc.cs#Run"}, keys);
    Assert.All(PlanParser.Parse(text).Tasks, t => Assert.Equal("G1", t.Group));
}
[Fact] public void Non_calling_members_are_not_included() =>
    Assert.DoesNotContain("#Idle", CallerFinder.Plan(TestPaths.Fixture("mini-corpus"), new[]{"Mini"},
        new PlanTask("T1","cs","Mini/IRepo.cs","Save","(seed)",null)));
```

실제 범주 A 사례에 대한 기대값은 테스트에 넣지 않는다. 그것이 측정 대상이기 때문이다.

- [ ] **Step 2: 실패 확인** — `dotnet test` → 컴파일 실패
- [ ] **Step 3: `CallerFinder.Plan` 구현**
- [ ] **Step 4: 통과 확인** — `dotnet test` → 전부 통과
- [ ] **Step 5: 커밋** — `git commit -m "spike(S6): arm C 구문 호출부 찾기"`

---

### Task 7: 실행기 — 승인 게이트, digest, 호출, 원문 저장

**Files:**
- Create: `spikes/S6-planning/S6Planning/Runner.cs`
- Test: `spikes/S6-planning/S6Planning.Tests/RunnerTests.cs`

**Interfaces:**
- Consumes: `GoldLoader`, `CorpusIndexBuilder`, `PlanPrompt`, `QwenChatTemplate`
- Produces:
  - `public sealed record RawRecord(string CaseId, string Arm, int Rep, string? ModelDigest, int? PromptTokens, string? Think, bool ThinkTruncated, string Output, string? DoneReason, int? EvalCount, double? Seconds);`
    - JSON 직렬화는 System.Text.Json camelCase, 들여쓰기로 한다.
  - `public static class ApprovalGate { public static string? Check(string casesDir, string goldCommit); }`
    - 통과하면 null, 거부하면 사유를 돌려준다.
    - `APPROVED.json` 형식은 `{"approvedBy":"...","approvedAt":"YYYY-MM-DD","goldCommit":"<40자>"}`이다.
  - `public static class Runner`
    - `public static string? CheckDigest(string? digest)`: `a7eb95c53bcf`로 시작하지 않으면 사유를 돌려준다.
    - `public static string Stage2Prompt(string stage1Raw, string thinkText, QwenChatTemplate t)`: `stage1Raw + thinkText + t.ThinkClose`
    - `public static Task<RawRecord> RunOnceAsync(HttpClient http, string model, GoldCase c, CorpusIndex idx, string arm, int rep, CancellationToken ct)`
- 호출은 `/api/generate`에 다음 본문으로 보낸다. 스트리밍은 쓰지 않는다.
  - `{"model", "prompt", "raw": true, "stream": false, "options": {temperature, top_p, top_k, repeat_penalty, presence_penalty, num_predict, num_ctx: 8192, stop}, "keep_alive": "10m"}`
- arm B에서 `done_reason == "length"`이면 `ThinkTruncated = true`이다.
- `run` 하위 명령:
  - 형식: `run --arm A|B --repeat 5 [--case <id>]`
  - 동작 순서:
    1. 게이트를 확인한다. goldCommit은 `git log -1 --format=%H -- cases/*/gold.*`를 프로세스로 실행해 얻는다.
    2. `/api/show`로 digest를 확인한다.
    3. 사례 × 반복을 순차로 실행한다.
    4. 원문을 `raw/<arm>/<case>/<rep>.json`에 저장한다. 이미 있으면 건너뛴다(재개용).

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
[Fact] public void Gate_refuses_without_approval_file() =>
    Assert.Contains("APPROVED.json", ApprovalGate.Check(TestPaths.Fixture("gate-missing"), new string('a', 40)));
[Fact] public void Gate_refuses_on_commit_mismatch() =>
    Assert.Contains("goldCommit", ApprovalGate.Check(TestPaths.Fixture("gate-approved"), new string('b', 40)));
[Fact] public void Gate_refuses_when_any_gold_is_draft() =>
    Assert.Contains("draft", ApprovalGate.Check(TestPaths.Fixture("gate-one-draft"), new string('a', 40)));
[Fact] public void Gate_passes_when_all_approved_and_commit_matches() =>
    Assert.Null(ApprovalGate.Check(TestPaths.Fixture("gate-approved"), new string('a', 40)));
[Fact] public void Real_cases_are_currently_refused() =>                 // 초안 상태 확인
    Assert.NotNull(ApprovalGate.Check(TestPaths.Cases, new string('a', 40)));
[Theory]
[InlineData("a7eb95c53bcf96b4bdd008d0fab4a5dac88047d9c1a7a9ab88ed453423fbd87c", true)]
[InlineData("sha256:a7eb95c53bcf96b4", false)]
[InlineData(null, false)]
public void Digest_must_be_field_model(string? d, bool ok) => Assert.Equal(ok, Runner.CheckDigest(d) is null);
[Fact] public void Stage2_closes_truncated_think()
{
    var t = QwenChatTemplate.LoadDefault();
    Assert.EndsWith("half thought" + t.ThinkClose, Runner.Stage2Prompt("P" + t.ThinkOpen, "half thought", t));
}
```

픽스처 `gate-*`는 `gold.md` 한두 개와 `APPROVED.json`(goldCommit `aaaa…`)으로 구성한다.

- [ ] **Step 2: 실패 확인** — `dotnet test` → 컴파일 실패
- [ ] **Step 3: `ApprovalGate`, `Runner`, `run` 명령 구현. `callers` 명령도 여기서 배선한다.**
  - 범주 A 사례마다 씨앗 `gold.Tasks[0]`으로 `CallerFinder.Plan`을 부른다.
  - 결과는 `raw/C/<case>/1.json`에 `RawRecord`로 저장한다(Output만 채우고 모델 칸은 null).
  - 실행 전에 같은 게이트를 거친다.
- [ ] **Step 4: 통과 확인** — `dotnet test` → 전부 통과
- [ ] **Step 5: 거부 동작 확인**
  - Run: `cd spikes/S6-planning && dotnet run --project S6Planning -- run --arm A --repeat 1`
  - Expected: 종료 코드 2와 `draft` 사유. 이 시점에서는 모델을 부르지 않는다.
- [ ] **Step 6: 커밋** — `git commit -m "spike(S6): 실행기·승인 게이트·digest 확인"`

---

### Task 8: 집계와 보고 표

**Files:**
- Create: `spikes/S6-planning/S6Planning/Report.cs`
- Test: `spikes/S6-planning/S6Planning.Tests/ReportTests.cs`

**Interfaces:**
- Consumes: `RawRecord`(Task 7), `Scorer`, `Verdict`(Task 5), `GoldLoader`, `CorpusIndexBuilder`
- Produces:
  - `public static class Report`
    - `public static IReadOnlyList<RunScore> ScoreAll(string rawDir, string casesDir, string corpusRoot);`
    - `public static string Csv(IReadOnlyList<RunScore> s);`: 헤더는 `RunScore` 속성 순서를 따른다.
    - `public static string Summary(IReadOnlyList<RunScore> s, IReadOnlyList<RawRecord> raws);`
- `Summary`의 마크다운 구성:
  1. arm × 범주 Usable 표(`n/15`, %)
  2. arm별 평균 Recall, Precision, Hallucinated 반복 수, Forbidden 반복 수, OrderViolations, ExampleCopy 수, ThinkTruncated 수
  3. arm별 `Verdict.Decide` 결과
  4. 범주 A에 대한 「arm C 재현율 vs arm A·B 평균 재현율」 한 줄
- `score` 명령은 `results/results.csv`를 쓰고, `report` 명령은 `results/summary.md`를 쓴다.

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
[Fact] public void Score_all_reads_raw_tree_and_matches_case_count()
{   // 픽스처 raw/A/d2-legacy-pending-history/1.json (정답과 같은 출력) 과 실제 cases
    var s = Report.ScoreAll(TestPaths.Fixture("raw-one"), TestPaths.Cases, TestPaths.Corpus);
    var r = Assert.Single(s); Assert.True(r.Usable); Assert.Equal('D', r.Category);
}
[Fact] public void Summary_contains_verdict_and_category_table()
{
    var md = Report.Summary(Fx.Scores(usable: 48, total: 60), Array.Empty<RawRecord>());
    Assert.Contains("| A |", md); Assert.Contains("PremiseRejected", md);
}
```

- [ ] **Step 2: 실패 확인** → **Step 3: 구현** → **Step 4: 통과 확인** (`dotnet test` 전부 통과)
- [ ] **Step 5: 커밋** — `git commit -m "spike(S6): 집계·보고 표"`

---

### Task 9: 측정 실행과 보고서 (사람 승인 뒤에만)

이 작업은 코드 작업이 아니다. **정답 승인 전에는 시작하지 않는다.** 서브에이전트 실행 중에 이 작업에 도달하면 멈추고 사람에게 넘긴다.

- [ ] **Step 1: 사람 승인 받기**
  - 사람이 12건의 `gold.md`와 `gold.constraints.json`을 검토한다. 고칠 곳이 있으면 고친다.
  - 각 파일을 `status: approved`로 바꾸고 커밋한다.
  - 그 커밋 해시로 `cases/APPROVED.json`을 쓰고, 이것을 별도로 커밋한다.
  - 이후 정답 파일은 바꾸지 않는다.
- [ ] **Step 2: 모델 확인**
  - Run: `curl -s localhost:11434/api/show -d '{"model":"qwen3.6:35b"}' | grep -o '"digest[^,]*'`
  - Expected: `a7eb95c53bcf`가 보인다. `run`도 같은 확인을 한다.
- [ ] **Step 3: 실행** (cwd `spikes/S6-planning`, 각 명령은 원문을 저장하고 중단 후 재개된다)
  - `dotnet run --project S6Planning -- run --arm A --repeat 5`
  - `dotnet run --project S6Planning -- run --arm B --repeat 5`
  - `dotnet run --project S6Planning -- callers`
  - Expected: `raw/A`, `raw/B`에 각 60개, `raw/C`에 3개
- [ ] **Step 4: 채점과 집계**
  - `dotnet run --project S6Planning -- score && dotnet run --project S6Planning -- report`
- [ ] **Step 5: 보고서 작성 `docs/s6-planning-probe.md`**
  - 모델 digest, 정답 커밋, 실행 일시, 각 arm의 판정(설계 §7 표 그대로), summary 표를 싣는다.
  - 실패 원문 예시는 범주마다 하나씩 싣는다.
  - 정답 결함이 발견되면 정답을 고치지 않고 「정답 결함」으로 표시한다. 그 사례를 뺀 수치도 함께 싣는다.
  - 설계 §6.3의 사람 확인 표본 목록(arm별 10개, 시드 고정 난수로 뽑음)을 싣고, 사람의 예/아니오 칸은 비워 둔다.
- [ ] **Step 6: 커밋**
  - `git add docs/s6-planning-probe.md && git commit -m "docs: S6 작업 분해 프로브 결과"`
  - `raw/`와 `results/`는 gitignore 대상이다. 보고서에 필요한 수치는 보고서에 옮긴다.
