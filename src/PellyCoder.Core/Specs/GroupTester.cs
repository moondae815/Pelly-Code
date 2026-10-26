using System.IO.Abstractions;
using System.Xml;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>
/// What a task group checks (design §7.3): every acceptance-criterion test linked to a group task, the tests whose RED the
/// group judges (first linked task in the group), the tests whose GREEN it judges (last linked task in the group), and the
/// expected breaks of those criteria. <see cref="ReportPrefix"/> is the absolute path prefix of the group's TRX reports
/// (e.g. <c>.pelly/runs/&lt;apply-id&gt;/group-tests/G1</c>); empty puts them under the workspace's run folder.
/// <see cref="ExpectedHashes"/> maps a plan test to the declaration hash recorded right before its criterion's first linked
/// task ran outside the group; the verdict compares a test without one to the group's baseline instead.
/// </summary>
public sealed record GroupTestPlan(IReadOnlyList<string> Tests, IReadOnlyList<string> RedNames, IReadOnlyList<string> GreenNames,
    IReadOnlyList<string> Breaks)
{
    public string ReportPrefix { get; init; } = "";

    public IReadOnlyDictionary<string, string>? ExpectedHashes { get; init; }
}

/// <summary>
/// The group's baseline test runs before any edit. <see cref="CompileRed"/> holds the indexes of the projects that did not
/// build because the named tests use code the group has still to write; <see cref="Red"/> maps every plan test to its
/// baseline state. <see cref="Unverifiable"/> (a Korean user message) means no verdict is possible (exit 4 before the edit).
/// <see cref="Tests"/> are the matched tests (for GREEN), <see cref="Notices"/> what the user is told before the edit.
/// </summary>
public sealed record GroupTestBaseline(IReadOnlyList<string> Projects, IReadOnlyList<TestRun?> Runs, IReadOnlySet<int> CompileRed,
    IReadOnlyDictionary<string, RedState> Red, string? Unverifiable)
{
    public IReadOnlyList<RedTest> Tests { get; init; } = [];
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>
    /// Plan test → its declaring file (absolute) and <see cref="TestDeclarationHash.Of"/> before any edit; a test whose
    /// hash could not be taken is absent (no reference to compare with).
    /// </summary>
    public IReadOnlyDictionary<string, (string File, string Hash)> Declarations { get; init; } =
        new Dictionary<string, (string File, string Hash)>(StringComparer.Ordinal);
}

/// <summary>
/// The group's test verdict after its build passed. <see cref="Green"/> and <see cref="TestBuildErrors"/> (a compile-RED project
/// that still does not build; <see cref="Unbuilt"/> maps each such project, workspace-relative, to the plan tests declared there); <see cref="Regressions"/>
/// are newly failing tests. <see cref="Unverifiable"/> (a Korean user message) means no verdict is possible.
/// </summary>
public sealed record GroupTestVerdict(string? Unverifiable, IReadOnlyList<GreenFailure> Green, IReadOnlyList<Diagnostic> TestBuildErrors,
    IReadOnlyList<TestCaseResult> Regressions, int BreaksExcluded)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Unbuilt { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Full names (without arguments) of the expected breaks left out; null when not reported (then only the count is known).</summary>
    public IReadOnlyList<string>? ExcludedNames { get; init; }

    /// <summary>
    /// The workspace-relative file of a plan test whose declaration changed while the group ran (checked only once GREEN and
    /// regressions passed); null when none did.
    /// </summary>
    public string? ChangedTestFile { get; init; }
}

/// <summary>Runs a task group's test projects before and after its edits (design §7.3).</summary>
public interface IGroupTester
{
    Task<GroupTestBaseline> BaselineAsync(Workspace ws, IReadOnlyList<string> scope, GroupTestPlan plan, CancellationToken ct);

    Task<GroupTestVerdict> VerifyAsync(Workspace ws, GroupTestBaseline baseline, GroupTestPlan plan, CancellationToken ct);
}

/// <summary>
/// Group test-first verification (design §5.3 compile RED, §7.3). The test projects are the scope's test projects (name containing
/// <c>Test</c>, or the scope's projects listed in <c>test.projects</c>) followed by the projects declaring the plan's tests. The
/// baseline judges RED; a test project that does not build there is a compile RED when its errors lie inside the named tests,
/// and is then judged strictly afterwards (every test must pass). The verdict runs the same projects again for GREEN and regressions.
/// </summary>
public sealed class GroupTester(IFileSystem fs, TestRunner runner, BuildToolResolver tools, TestOptions options, Func<bool> isWindows)
    : IGroupTester
{
    /// <summary>Notice for a compile-RED project; followed by its workspace-relative path.</summary>
    public const string StrictNotice = "그 테스트 프로젝트의 모든 테스트가 통과해야 합니다 (원본 상태에서 빌드되지 않아 비교할 결과가 없음)";

    private int _verifies;

    public async Task<GroupTestBaseline> BaselineAsync(Workspace ws, IReadOnlyList<string> scope, GroupTestPlan plan, CancellationToken ct)
    {
        GroupTestBaseline Unverifiable(IReadOnlyList<string> projects, string message) =>
            new(projects, [], new HashSet<int>(), new Dictionary<string, RedState>(StringComparer.Ordinal), message);

        // Where every named test is declared: the same search as LOCATE and spec check.
        var decls = new Dictionary<string, TestMethodDecl>(StringComparer.Ordinal);
        foreach (var name in plan.Tests)
        {
            IReadOnlyList<TestMethodDecl> found;
            try
            {
                found = TestMethodIndex.Find(fs, ws.Root, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Unverifiable([], $"테스트 메서드를 찾는 중 파일을 읽을 수 없습니다 ({ex.Message})");
            }
            var fullNames = found.Select(d => d.FullName).Distinct(StringComparer.Ordinal).ToList();
            if (fullNames.Count == 0)
                return Unverifiable([], $"테스트 {name}을(를) 작업 공간에서 찾지 못했습니다 (테스트 특성 Fact·Theory·Test·TestCase·TestMethod·DataTestMethod가 붙은 메서드만 찾습니다)");
            if (fullNames.Count > 1)
                return Unverifiable([], $"테스트 이름 {name}이(가) 여러 테스트와 맞습니다: {string.Join(", ", fullNames)}");
            if (found.FirstOrDefault(d => d.Csproj is null) is not null)
                return Unverifiable([], $"테스트 {name}이(가) 어떤 테스트 프로젝트에도 속하지 않습니다");
            decls[name] = found[0] with { Csproj = fs.Path.GetFullPath(found[0].Csproj!), File = fs.Path.GetFullPath(found[0].File) };
        }

        var projects = Projects(ws, scope, decls.Values);
        var resolved = await ResolveToolsAsync(projects, ct);
        if (resolved.Error is { } toolError) return Unverifiable(projects, toolError);

        var runs = new TestRun?[projects.Count];
        var compileRed = new HashSet<int>();
        var notices = new List<string>();
        for (var i = 0; i < projects.Count; i++)
        {
            var run = await runner.RunAsync(resolved.Tools[i], projects[i], ws.Root, Trx(ws, plan, $"baseline-{i + 1}"), ct);
            if (run.TimedOut)
                return Unverifiable(projects, $"기준 테스트를 실행하지 못해 지정한 테스트를 판정할 수 없습니다: {TestStep.TimedOutMessage(options)}");
            if (run.Error is { } error)
                return Unverifiable(projects, $"기준 테스트를 실행하지 못해 지정한 테스트를 판정할 수 없습니다: {error}");
            runs[i] = run;
            if (run.BuildErrors.Count == 0) continue;

            var inProject = decls.Values.Where(d => PathComparers.ForOs.Equals(d.Csproj, projects[i])).ToList();
            var verdict = CompileRed.Judge(fs, ws.Root, projects[i], run.BuildErrors, inProject);
            if (!verdict.IsCompileRed)
                return Unverifiable(projects, verdict.Unverifiable ?? "테스트 프로젝트가 원본 상태에서 빌드되지 않습니다");
            compileRed.Add(i);
            notices.Add($"{StrictNotice}: {Relative(ws, projects[i])}");
        }

        // RED over the projects that built; a name declared in a compile-RED project is not looked up in the results.
        var built = Enumerable.Range(0, projects.Count).Where(i => !compileRed.Contains(i)).ToList();
        bool InCompileRed(string name) => compileRed.Any(i => PathComparers.ForOs.Equals(decls[name].Csproj, projects[i]));
        var looked = plan.Tests.Where(n => !InCompileRed(n)).ToList();
        var red = TestFirstJudge.Red(built.Select(i => projects[i]).ToList(), built.Select(i => runs[i]).ToList(), looked);
        if (red.Unverifiable is { } message) return Unverifiable(projects, message);

        var states = new Dictionary<string, RedState>(StringComparer.Ordinal);
        var tests = new List<RedTest>();
        foreach (var t in red.Tests)
        {
            states[t.Name] = t.State;
            tests.Add(t with { ProjectIndex = built[t.ProjectIndex] });
        }
        foreach (var name in plan.Tests.Where(InCompileRed))
        {
            states[name] = RedState.RedCompile;
            var index = Enumerable.Range(0, projects.Count).First(i => PathComparers.ForOs.Equals(decls[name].Csproj, projects[i]));
            tests.Add(new RedTest(name, decls[name].FullName, index, RedState.RedCompile, null));
        }
        notices.AddRange(TestFirstJudge.BreakWarnings(built.Select(i => runs[i]).ToList(), plan.Breaks));

        // The test-change guard's reference (design V2-2c): every plan test's declaration as it stands before the group edits.
        var declarations = new Dictionary<string, (string File, string Hash)>(StringComparer.Ordinal);
        foreach (var (name, decl) in decls)
            if (HashOf(ws, name) is { } hash) declarations[name] = (decl.File, hash);

        return new GroupTestBaseline(projects, runs, compileRed, states, null) { Tests = tests, Notices = notices, Declarations = declarations };
    }

    public async Task<GroupTestVerdict> VerifyAsync(Workspace ws, GroupTestBaseline baseline, GroupTestPlan plan, CancellationToken ct)
    {
        static GroupTestVerdict Unverifiable(string message) => new(message, [], [], [], 0);

        var projects = baseline.Projects;
        var resolved = await ResolveToolsAsync(projects, ct);
        if (resolved.Error is { } toolError) return Unverifiable(toolError);

        int verify = Interlocked.Increment(ref _verifies);
        // Every named test fails in the baseline or is judged by GREEN: none of them is a regression.
        // As in TestStep: the expected breaks, and the tests GREEN judges. A test with another role that passed before and fails
        // now is a regression; one failing before never counts (in a compile-RED project nothing ran, so its named tests count as failing).
        var excluded = plan.Breaks.Concat(plan.GreenNames).Distinct(StringComparer.Ordinal).ToList();
        var hits = new HashSet<string>(StringComparer.Ordinal); // excluded results, by name without arguments
        var runs = new TestRun?[projects.Count];
        var unbuilt = new HashSet<int>();
        var buildErrors = new List<Diagnostic>();
        var regressions = new List<TestCaseResult>();
        for (var i = 0; i < projects.Count; i++)
        {
            var before = baseline.Runs[i]!;
            var run = await runner.RunAsync(resolved.Tools[i], projects[i], ws.Root, Trx(ws, plan, $"verify-{verify}-{i + 1}"), ct);
            if (run.TimedOut) return Unverifiable(TestStep.TimedOutMessage(options));
            var strict = baseline.CompileRed.Contains(i);
            if (run.ReportMissing && before.PerTestResults && !strict)
            {
                // A crashed test host (no report) is a test failure the model can fix, not a broken environment.
                Collect(before, run, projects[i]);
                continue;
            }
            if (run.Error is { } error) return Unverifiable($"테스트를 실행하지 못했습니다: {error}");

            if (run.BuildErrors.Count > 0)
            {
                var errors = strict ? run.BuildErrors : BaselineDiff.New(before.BuildErrors, run.BuildErrors);
                if (errors.FirstOrDefault(e => !BuildMessages.IsCompilerError(e)) is { } blocker)
                    return Unverifiable(BuildMessages.Unverifiable(blocker, "테스트 프로젝트가 빌드되는지 먼저 확인하세요"));
                unbuilt.Add(i);
                buildErrors.AddRange(errors);
                continue;
            }

            runs[i] = run;
            if (strict)
            {
                if (!run.PerTestResults)
                    return Unverifiable("테스트별 결과가 없어 지정한 테스트를 판정할 수 없습니다 (레거시는 test.command에 {trx}를 넣으세요)");
                var strictExcluded = excluded.Concat(baseline.Tests.Where(t => t.ProjectIndex == i).Select(t => t.FullName)).ToList();
                regressions.AddRange(TestRegressions.Strict(run, strictExcluded));
                foreach (var r in run.Results.Where(r => !r.Passed && plan.Breaks.Any(b => TestNames.Matches(b, r.FullName))))
                    hits.Add(TestNames.WithoutArguments(r.FullName));
                continue;
            }
            Collect(before, run, projects[i]);
        }

        void Collect(TestRun before, TestRun run, string project)
        {
            var (found, exitOnly) = TestRegressions.Find(before, run, excluded, hits);
            regressions.AddRange(found);
            if (exitOnly) regressions.Add(new TestCaseResult(fs.Path.GetFileName(project), false, $"exit code {run.ExitCode}"));
        }

        var green = baseline.Tests
            .Where(t => plan.GreenNames.Contains(t.Name, StringComparer.Ordinal) && !unbuilt.Contains(t.ProjectIndex)).ToList();
        var unbuiltTests = unbuilt.ToDictionary(i => Relative(ws, projects[i]),
            i => (IReadOnlyList<string>)baseline.Tests.Where(t => t.ProjectIndex == i).Select(t => t.Name).ToList());
        var breakHits = hits.Where(h => plan.Breaks.Any(b => TestNames.Matches(b, h))).Order(StringComparer.Ordinal).ToList();
        var greenFailures = TestFirstJudge.Green(green, runs);
        var distinctRegressions = regressions.DistinctBy(r => r.FullName).ToList();
        // As in TestStep: the test-change guard comes after GREEN and regressions passed.
        var changed = greenFailures.Count == 0 && buildErrors.Count == 0 && distinctRegressions.Count == 0
            ? ChangedTestFile(ws, baseline, plan)
            : null;
        return new GroupTestVerdict(null, greenFailures, buildErrors.Distinct().ToList(), distinctRegressions, breakHits.Count)
        {
            Unbuilt = unbuiltTests,
            ExcludedNames = breakHits,
            ChangedTestFile = changed,
        };
    }

    /// <summary>
    /// The workspace-relative file of the first plan test whose declaration no longer hashes to its reference: the hash its
    /// criterion's first linked task outside the group started from (<see cref="GroupTestPlan.ExpectedHashes"/>), else the
    /// baseline's. Null when every test still matches; a test with neither reference is not checked.
    /// </summary>
    private string? ChangedTestFile(Workspace ws, GroupTestBaseline baseline, GroupTestPlan plan)
    {
        foreach (var name in plan.Tests)
        {
            var known = baseline.Declarations.TryGetValue(name, out var declaration);
            string? expected = plan.ExpectedHashes?.GetValueOrDefault(name) ?? (known ? declaration.Hash : null);
            if (expected is null || HashOf(ws, name) == expected) continue;
            // Name the declaring file found now; a vanished declaration falls back to the one the baseline saw.
            string? file;
            try { file = TestMethodIndex.Find(fs, ws.Root, name).Select(d => d.File).FirstOrDefault(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { file = null; }
            file ??= known ? declaration.File : null;
            return file is null ? name : fs.Path.GetRelativePath(ws.Root, fs.Path.GetFullPath(file));
        }
        return null;
    }

    /// <summary>The test's declaration hash; null when it cannot be taken (not found, or a file cannot be read).</summary>
    private string? HashOf(Workspace ws, string name)
    {
        try { return TestDeclarationHash.Of(fs, ws.Root, name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The scope's test projects (or its <c>test.projects</c>), then the projects declaring the named tests.</summary>
    private List<string> Projects(Workspace ws, IReadOnlyList<string> scope, IEnumerable<TestMethodDecl> decls)
    {
        IEnumerable<string> fromScope;
        if (options.Projects.Count > 0)
        {
            var listed = options.Projects
                .Select(p => fs.Path.GetFullPath(fs.Path.Combine(ws.Root, p.Replace('\\', fs.Path.DirectorySeparatorChar).Replace('/', fs.Path.DirectorySeparatorChar))))
                .ToHashSet(PathComparers.ForOs);
            fromScope = scope.Where(listed.Contains);
        }
        else
        {
            fromScope = scope.Where(p => fs.Path.GetFileNameWithoutExtension(p).Contains("Test", StringComparison.OrdinalIgnoreCase));
        }
        return fromScope.Select(fs.Path.GetFullPath).Concat(decls.Select(d => d.Csproj!)).Distinct(PathComparers.ForOs).ToList();
    }

    /// <summary>The build tool of every project, or the reason a project cannot be tested here (checked before any run).</summary>
    private async Task<(IReadOnlyList<BuildTool> Tools, string? Error)> ResolveToolsAsync(IReadOnlyList<string> projects, CancellationToken ct)
    {
        var styles = new List<ProjectStyle>(projects.Count);
        foreach (var project in projects)
        {
            ProjectStyle style;
            try
            {
                style = ProjectStyleDetector.Detect(fs, project);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                return ([], $"프로젝트 파일을 읽을 수 없습니다 ({ex.Message})");
            }
            if (style == ProjectStyle.Legacy && !isWindows())
                return ([], "레거시 테스트는 Windows에서만 실행합니다 — 테스트 우선 검증을 할 수 없습니다");
            styles.Add(style);
        }

        var resolved = new Dictionary<ProjectStyle, BuildTool>();
        foreach (var style in styles.Distinct())
        {
            var resolution = await tools.ResolveAsync(style, ct);
            if (resolution.Tool is not { } tool) return ([], resolution.Error!);
            resolved[style] = tool;
        }
        return (styles.Select(s => resolved[s]).ToList(), null);
    }

    private string Trx(Workspace ws, GroupTestPlan plan, string name)
    {
        var prefix = plan.ReportPrefix.Length > 0 ? plan.ReportPrefix : fs.Path.Combine(ws.PellyDir, "runs", "group-tests", "group");
        return fs.Path.GetFullPath(prefix + "-" + name + ".trx");
    }

    private string Relative(Workspace ws, string project) => fs.Path.GetRelativePath(ws.Root, project).Replace('\\', '/');
}
