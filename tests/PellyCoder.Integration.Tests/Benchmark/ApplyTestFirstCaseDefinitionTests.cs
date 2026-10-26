using System.Collections.Concurrent;
using System.IO.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Verification;
using PellyCoder.Eval;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.Infrastructure;
using PellyCoder.Integration.Tests.Testing;

namespace PellyCoder.Integration.Tests.Benchmark;

/// <summary>
/// 테스트 우선 apply 사례(eval/benchmark/apply-test-first, 설계 V2-2c §5.2)의 정의 검사: 수용 기준 테스트는 원본 코퍼스 + 사례의
/// <c>files/</c>에서 정말 RED여야 하고(t4는 이미 통과, t3은 테스트 프로젝트의 컴파일 RED), <c>breaks:</c>는 원본에서 통과해야 한다
/// (지금 동작을 실제로 고정한다). 실행기와 같은 작업 복사본(지원 파일, <c>corpus/</c>, <c>.git</c> 표시, <c>files/</c> 덮어쓰기,
/// <c>Modern.sln</c> 복원)을 임시 폴더에 만들고, 독립 재실행과 같은 경로(<see cref="TestMethodIndex"/> → <see cref="TestRunner"/>)로 돌린다.
/// </summary>
public sealed class ApplyTestFirstCaseDefinitionTests(ApplyTestFirstCaseDefinitionTests.CaseRuns runs)
    : IClassFixture<ApplyTestFirstCaseDefinitionTests.CaseRuns>
{
    private static readonly string EvalRoot = Directory.GetParent(CorpusPaths.Root)!.FullName;

    /// <summary>The Modern cases whose criteria really run on macOS; t6 needs SQL only for its sql tasks, its C# test runs anywhere.</summary>
    private static readonly string[] ModernCases = ["t1", "t2", "t3", "t4", "t5", "t6", "t7"];

    [Fact]
    public async Task ModernCases_RedIsRealOnOriginal()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var id in ModernCases)
        {
            var run = await runs.GetAsync(id, ct);
            foreach (var expected in run.Case.Expected!)
            {
                var name = run.TestOf(expected.AcId);
                switch (expected.Red)
                {
                    case nameof(RedState.Red):
                        Assert.True(run.Outcome(name) == false, $"{id} {expected.AcId}: {name}이(가) 원본에서 실패해야 합니다 ({run.Describe(name)})");
                        break;
                    case nameof(RedState.AlreadyPassed):
                        Assert.True(run.Outcome(name) == true, $"{id} {expected.AcId}: {name}이(가) 원본에서 이미 통과해야 합니다 ({run.Describe(name)})");
                        break;
                    case nameof(RedState.RedCompile):
                        // Only compiler errors, all in the file that declares the criterion's test (the new signature).
                        var decl = Assert.Single(run.Declarations(name));
                        var errors = run.BuildErrorsOf(decl.Csproj!);
                        Assert.True(errors.Count > 0, $"{id} {expected.AcId}: 테스트 프로젝트가 원본에서 빌드되면 안 됩니다");
                        Assert.All(errors, e => Assert.True(BuildMessages.IsCompilerError(e), $"{id}: {e.Code} {e.Message}"));
                        Assert.All(errors, e => Assert.Equal(Path.GetFileName(decl.File), Path.GetFileName(e.File)));
                        break;
                    case nameof(RedState.NotChecked):
                        // No linked cs task (TestUnverified): never run by pelly, so nothing to define.
                        Assert.Equal(nameof(AcOutcome.Unverified), expected.Outcome);
                        break;
                    default:
                        Assert.Fail($"{id} {expected.AcId}: 알 수 없는 기대 RED {expected.Red}");
                        break;
                }
            }
        }
    }

    [Fact]
    public async Task Cases_BreaksPassOnOriginal()
    {
        var ct = TestContext.Current.CancellationToken;
        var checkedBreaks = 0;
        foreach (var id in ModernCases)
        {
            var c = runs.Case(id);
            var breaks = ChangeSpecParser.Parse(c.SpecText).Criteria.SelectMany(a => a.BreakNames).ToList();
            if (breaks.Count == 0) continue;
            var run = await runs.GetAsync(id, ct);
            foreach (var name in breaks)
            {
                Assert.True(run.Outcome(name) == true, $"{id}: breaks {name}이(가) 원본에서 통과해야 합니다 ({run.Describe(name)})");
                checkedBreaks++;
            }
        }
        Assert.True(checkedBreaks > 0, "breaks:가 있는 사례(t2)가 없습니다");
    }

    /// <summary>t8 (Legacy, MSTest through the case's <c>test.command</c>): Windows only, after <c>eval/corpus/Legacy/build.ps1</c>.</summary>
    [WindowsFact]
    public async Task LegacyCase_RedIsRealOnOriginal()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.True(Directory.Exists(CorpusPaths.Get("Legacy/packages")),
            "eval/corpus/Legacy/packages 가 없습니다. 먼저 eval/corpus/Legacy/build.ps1 을 실행하세요.");
        var run = await runs.GetAsync("t8", ct);
        var expected = Assert.Single(run.Case.Expected!);
        Assert.Equal(nameof(RedState.Red), expected.Red);
        var name = run.TestOf(expected.AcId);
        Assert.True(run.Outcome(name) == false, $"t8: {name}이(가) 원본에서 실패해야 합니다 ({run.Describe(name)})");
    }

    /// <summary>
    /// One work copy and one test run per case, shared by the tests of this class: the projects declaring any of the case's
    /// <c>test:</c> or <c>breaks:</c> names, each built and run once.
    /// </summary>
    public sealed class CaseRuns : IDisposable
    {
        private readonly string _created = Path.Combine(Path.GetTempPath(), "pelly-tf-def-" + Guid.NewGuid().ToString("N")[..8]);
        private readonly ConcurrentDictionary<string, Lazy<Task<CaseRun>>> _runs = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, ApplyCase> _cases;
        private readonly BenchmarkSuite _suite;

        public CaseRuns()
        {
            _suite = BenchmarkSuite.Load(Path.Combine(EvalRoot, "benchmark")).Suite
                ?? throw new InvalidDataException("내장 스위트(eval/benchmark/suite.json)를 읽을 수 없습니다");
            var dir = Path.Combine(_suite.Dir, "apply-test-first");
            _cases = ApplyBenchmark.LoadTestFirstCases(dir, null, ApplyBenchmark.LoadApproved(dir)).ToDictionary(c => c.Id, StringComparer.Ordinal);
            Directory.CreateDirectory(_created);
        }

        public ApplyCase Case(string id) => _cases[id];

        public Task<CaseRun> GetAsync(string id, CancellationToken ct) =>
            _runs.GetOrAdd(id, _ => new Lazy<Task<CaseRun>>(() => RunAsync(_cases[id], ct))).Value;

        private async Task<CaseRun> RunAsync(ApplyCase c, CancellationToken ct)
        {
            // Link-free root: through the macOS /var -> /private/var link the restore skips the transitive ProjectReference.
            var caseDir = Path.Combine(TestRunnerSdkTests.RealPath(_created), c.Id);
            var corpus = Path.Combine(caseDir, "corpus");
            Directory.CreateDirectory(caseDir);
            foreach (var support in _suite.SupportFiles) File.Copy(support, Path.Combine(caseDir, Path.GetFileName(support)));
            WorkCopy.CopyDirectory(CorpusPaths.Root, corpus);
            Directory.CreateDirectory(Path.Combine(corpus, ".git"));
            WorkCopy.Overlay(c.FilesDir!, corpus);

            var fs = new FileSystem();
            var processes = new SystemProcessRunner(TimeProvider.System);
            if (c.Scope.Contains("Modern") && SolutionRestore.Pick(fs, Path.Combine(corpus, "Modern")) is { } sln)
            {
                var restore = await processes.RunAsync(SolutionRestore.Spec(sln, Path.GetDirectoryName(sln)!), ct);
                Assert.True(restore.ExitCode == 0, $"{c.Id}: 복원 실패 {restore.StdOut}{restore.StdErr}");
            }

            var spec = ChangeSpecParser.Parse(c.SpecText);
            var names = spec.Criteria.Where(a => a.Test is not null).SelectMany(a => a.BreakNames.Prepend(a.Test!)).Distinct(StringComparer.Ordinal).ToList();
            var decls = names.ToDictionary(n => n, n => TestMethodIndex.Find(fs, corpus, n), StringComparer.Ordinal);
            var projects = decls.Values.SelectMany(d => d).Select(d => d.Csproj ?? throw new InvalidDataException($"{c.Id}: {d.FullName}의 프로젝트가 없습니다"))
                .Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToList();

            // The same resolution the apply benchmark does: CI's Windows runner has no vstest.console.exe on PATH.
            var command = c.TestCommand is { } raw ? await VstestCommand.ResolveAsync(raw, processes, fs, ct) : null;
            var options = new PellyOptions { Test = new TestOptions { Command = command } };
            var runner = new TestRunner(processes, options.Build, options.Test);
            var tools = new BuildToolResolver(fs, processes, options.Build);
            var results = new Dictionary<string, TestRun>(StringComparer.Ordinal);
            for (var i = 0; i < projects.Count; i++)
            {
                var resolution = await tools.ResolveAsync(ProjectStyleDetector.Detect(fs, projects[i]), ct);
                Assert.True(resolution.Tool is not null, $"{c.Id}: {resolution.Error}");
                var run = await runner.RunAsync(resolution.Tool!, projects[i], corpus, Path.Combine(caseDir, "trx", $"{i + 1}.trx"), ct);
                Assert.True(run.Error is null && !run.TimedOut, $"{c.Id} {projects[i]}: {run.Error}");
                results[projects[i]] = run;
            }
            return new CaseRun(c, spec, decls, results);
        }

        public void Dispose()
        {
            try { Directory.Delete(_created, recursive: true); }
            catch (IOException) { /* best effort: MSBuild node reuse may still hold files */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A case's test runs on the original corpus with its <c>files/</c>.</summary>
    public sealed record CaseRun(ApplyCase Case, ChangeSpec Spec, IReadOnlyDictionary<string, IReadOnlyList<TestMethodDecl>> Decls,
        IReadOnlyDictionary<string, TestRun> Runs)
    {
        public string TestOf(string acId) => Spec.Criteria.Single(a => a.Id == acId).Test!;

        public IReadOnlyList<TestMethodDecl> Declarations(string name) => Decls[name];

        public IReadOnlyList<Diagnostic> BuildErrorsOf(string csproj) => Runs[Path.GetFullPath(csproj)].BuildErrors;

        /// <summary>True: every matching result passed; false: one failed; null: no result (not built, not found, skipped).</summary>
        public bool? Outcome(string name)
        {
            var matched = Matches(name);
            return matched.Count == 0 ? null : matched.All(r => r.Passed);
        }

        public string Describe(string name)
        {
            var matched = Matches(name);
            if (matched.Count > 0) return string.Join("; ", matched.Select(r => $"{r.FullName} {(r.Passed ? "통과" : "실패")} {r.Message}"));
            var errors = Runs.Values.SelectMany(r => r.BuildErrors).Take(3).Select(e => $"{e.Code} {e.File}: {e.Message}");
            return $"결과 없음 (선언 {Decls[name].Count}곳; 빌드 오류: {string.Join("; ", errors)})";
        }

        private List<TestCaseResult> Matches(string name) =>
            Decls[name].Select(d => Runs[Path.GetFullPath(d.Csproj!)]).Distinct()
                .SelectMany(r => r.Results).Where(r => TestNames.Matches(name, r.FullName)).ToList();
    }
}
