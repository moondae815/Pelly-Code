namespace PellyCoder.Core.Testing;

/// <summary>Baseline state of a named test. <c>RedCompile</c> and <c>Skipped</c> are kept last so stored values never shift.</summary>
public enum RedState { NotChecked, Red, AlreadyPassed, RedCompile, Skipped }

public enum GreenState { NotChecked, Passed, Failed }

/// <summary>One test named by the spec: its matched full name (arguments cut), the project it ran in and its baseline state.</summary>
public sealed record RedTest(string Name, string FullName, int ProjectIndex, RedState State, string? FirstMessage);

/// <summary>RED verdict. <see cref="Unverifiable"/> (a Korean user message, exit 4) is set together with <see cref="FailureKind"/> when no verdict is possible.</summary>
public sealed record RedResult(IReadOnlyList<RedTest> Tests, string? Unverifiable, string? FailureKind);

/// <summary>A named test that did not pass after the change; <c>Detail</c> is "not run" when it is absent or skipped.</summary>
public sealed record GreenFailure(string FullName, string? Detail, string? StackTrace);

/// <summary>Pure verdicts for test-first verification over already-collected test runs.</summary>
public static class TestFirstJudge
{
    private const string NotRun = "not run";

    private static RedResult Unverifiable(string message, string kind) => new([], message, kind);

    public static RedResult Red(IReadOnlyList<string> projects, IReadOnlyList<TestRun?> baselines, IReadOnlyList<string> names)
    {
        // Rules 1-3 apply to every project: without knowing which project holds the named test, none can be left out.
        for (var i = 0; i < projects.Count; i++)
        {
            var run = i < baselines.Count ? baselines[i] : null;
            if (run is null)
                return Unverifiable("기준 테스트를 실행하지 못해 지정한 테스트를 판정할 수 없습니다", "TestUnverifiable");
            if (run.BuildErrors.Count > 0)
                return Unverifiable("테스트 프로젝트가 원본 상태에서 빌드되지 않습니다 — 새 시그니처로 쓴 테스트는 pelly apply의 작업 그룹에서만 받습니다", "TestUnverifiable");
            if (!run.PerTestResults)
                return Unverifiable("테스트별 결과가 없어 지정한 테스트를 판정할 수 없습니다 (레거시는 test.command에 {trx}를 넣으세요)", "TestUnverifiable");
        }

        var tests = new List<RedTest>();
        foreach (var name in names)
        {
            var matches = new List<(int Project, TestCaseResult Result)>();
            for (var i = 0; i < projects.Count; i++)
                foreach (var result in baselines[i]!.Results)
                    if (TestNames.Matches(name, result.FullName))
                        matches.Add((i, result));

            var distinct = matches.Select(m => (m.Project, Full: TestNames.WithoutArguments(m.Result.FullName))).Distinct().ToList();
            if (distinct.Count > 1)
            {
                var candidates = string.Join(", ", distinct.Select(d => d.Full).Distinct());
                return Unverifiable($"테스트 이름 {name}이(가) 여러 테스트와 맞습니다: {candidates}", "TestAmbiguous");
            }

            // A skipped row never counts as run: a test with any skipped row cannot be judged, matched rows or not.
            var skipped = baselines.Any(b => b!.Skipped?.Any(s => TestNames.Matches(name, s)) == true);
            if (skipped)
                return Unverifiable($"테스트 {name}이(가) 건너뛰도록 표시되어 있습니다", "TestUnverifiable");

            if (distinct.Count == 0)
                return Unverifiable($"테스트 {name}을(를) 테스트 실행 결과에서 찾지 못했습니다 (test.projects 설정을 확인하세요)", "TestNotFound");

            var firstFailure = matches.Select(m => m.Result).FirstOrDefault(r => !r.Passed);
            tests.Add(new RedTest(name, distinct[0].Full, distinct[0].Project,
                firstFailure is null ? RedState.AlreadyPassed : RedState.Red, firstFailure?.Message));
        }

        return new RedResult(tests, null, null);
    }

    public static IReadOnlyList<GreenFailure> Green(IReadOnlyList<RedTest> tests, IReadOnlyList<TestRun?> runs)
    {
        var failures = new List<GreenFailure>();
        foreach (var test in tests)
        {
            var run = test.ProjectIndex < runs.Count ? runs[test.ProjectIndex] : null;
            if (run is null)
            {
                failures.Add(new GreenFailure(test.FullName, NotRun, null));
                continue;
            }

            var rows = run.Results.Where(r => TestNames.WithoutArguments(r.FullName) == test.FullName).ToList();
            var partlySkipped = run.Skipped?.Any(n => TestNames.WithoutArguments(n) == test.FullName) == true;
            if (rows.Count == 0 || partlySkipped)
            {
                failures.Add(new GreenFailure(test.FullName, NotRun, null));
                continue;
            }

            var failed = rows.FirstOrDefault(r => !r.Passed);
            if (failed is not null)
                failures.Add(new GreenFailure(test.FullName, failed.Detail ?? failed.Message, failed.StackTrace));
        }

        return failures;
    }

    public static IReadOnlyList<string> BreakWarnings(IReadOnlyList<TestRun?> baselines, IReadOnlyList<string> breaks)
    {
        var warnings = new List<string>();
        foreach (var name in breaks)
        {
            var passes = baselines.Any(b => b is not null && b.Results.Any(r => r.Passed && TestNames.Matches(name, r.FullName)));
            if (!passes)
                warnings.Add($"깨질 것으로 정한 테스트 {name}이(가) 원본 상태에서 통과하지 않습니다");
        }

        return warnings;
    }
}
