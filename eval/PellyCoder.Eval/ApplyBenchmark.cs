using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Eval;

/// <summary>
/// One benchmark spec (<c>eval/benchmark/apply/&lt;id&gt;.md</c>, or a test-first case folder
/// <c>eval/benchmark/apply-test-first/&lt;id&gt;/</c>): its front matter scope and the shape of its tasks.
/// </summary>
/// <param name="Scope">The corpus roots the spec touches (<c>Modern</c>, <c>Legacy</c>, <c>Sql</c>), as the S6 gold lists them.</param>
/// <param name="SpecText">The spec as written to the work copy's <c>changes/&lt;id&gt;.md</c>.</param>
/// <param name="FilesDir">The case folder's <c>files/</c>, copied over the work copy's <c>corpus/</c> (same relative paths); null without one.</param>
/// <param name="Expected">The case's <c>expected.json</c>: shown beside the actual results, never used to judge.</param>
/// <param name="Approved">False for a folder case not listed in <c>APPROVED.json</c> (the summary is then preliminary).</param>
/// <param name="TestCommand">The <c>test.command</c> of the case folder's <c>pelly.json</c> fragment, merged into the options of that case's run only; null without one.</param>
public sealed record ApplyCase(string Id, IReadOnlyList<string> Scope, string SpecText, int TaskCount, bool HasSql, bool HasGroup,
    string? FilesDir = null, IReadOnlyList<ExpectedCriterion>? Expected = null, bool Approved = true, string? TestCommand = null);

/// <summary>One entry of a case's <c>expected.json</c>: the criterion's expected RED state and outcome (enum names).</summary>
public sealed record ExpectedCriterion(string AcId, string Red, string Outcome);

/// <summary>
/// The eval's own re-run of a Met criterion's <c>test:</c> outside pelly: <see cref="Verdict"/> is <see cref="ApplyBenchmark.TestPassed"/>,
/// <see cref="ApplyBenchmark.TestMismatch"/> (a test judge mismatch), <see cref="ApplyBenchmark.TestSkipped"/> (legacy off Windows) or
/// <see cref="ApplyBenchmark.TestUnverifiable"/>; <see cref="Detail"/> says why when it did not pass.
/// </summary>
public sealed record AcTestCheck(string AcId, string Verdict, string? Detail);

/// <summary>
/// One case run. <paramref name="Outcome"/>: <c>completed</c> (exit 0 and archived), <c>failed</c>, <c>skipped</c> (never run,
/// <paramref name="SkipReason"/> says why) or <c>error</c> (the runner itself failed, <paramref name="Error"/>).
/// <paramref name="Archived"/> and <paramref name="Tasks"/> come from the apply's own <c>apply.json</c>.
/// <paramref name="IndependentBuildOk"/> is the eval's own rebuild of an exit-0 apply: false is a verification invariant
/// violation, null means not judged (<paramref name="IndependentDetail"/> says why).
/// <paramref name="Criteria"/> are the criteria of the run's <c>apply.json</c>; <paramref name="TestChecks"/> the independent re-run of
/// each Met criterion's test, <paramref name="TestJudgeMismatches"/> how many of them did not pass (an invariant violation like a
/// failed independent build). The three are left out of <c>apply-results.json</c> when unset.
/// </summary>
public sealed record ApplyCaseResult(string Id, int Repeat, string Outcome, string? SkipReason,
    int ExitCode, bool Archived, int TaskCount, IReadOnlyList<ApplyTaskResult> Tasks, bool? IndependentBuildOk, double Seconds,
    string? Error = null, string? IndependentDetail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AcResult>? Criteria = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int TestJudgeMismatches = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AcTestCheck>? TestChecks = null);

/// <summary>What the apply benchmark needs besides the case: the composition the real command or a test supplies.</summary>
/// <param name="RunFactory">Builds the per-task run delegate (the v1 orchestrator in <c>pelly-eval apply</c>).</param>
/// <param name="Verifier">Builds the independent check's projects.</param>
/// <param name="CorpusDir">The pristine corpus (<c>eval/corpus</c>): copied per case and compared after the apply.</param>
/// <param name="SupportFiles">Copied by file name beside the work corpus (the suite's <c>supportFiles</c>).</param>
public sealed record ApplyBenchSetup(
    IFileSystem Fs, PellyOptions Options, IProcessRunner Processes, TimeProvider Time, Func<bool> IsWindows, bool SqlAvailable,
    Func<PellyOptions, Func<RunRequest, CancellationToken, Task<RunResult>>> RunFactory, IGroupVerifier Verifier,
    string CorpusDir, IReadOnlyList<string> SupportFiles, Action<string> Log);

/// <summary>
/// <c>pelly-eval apply</c> (design §8): each approved S6 gold spec runs end to end through the same <see cref="SpecRunner"/>
/// composition as <c>pelly apply</c>, in its own copy of the corpus, and an exit-0 apply is rebuilt independently.
/// </summary>
public static class ApplyBenchmark
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Errored = "error";

    public const string WindowsOnly = "Windows 전용";
    public const string NoSql = "SQL 연결 없음";

    /// <summary><see cref="ApplyCaseResult.IndependentDetail"/> when the apply changed no C# file (nothing to rebuild).</summary>
    public const string NoCsChange = "C# 변경 없음";

    /// <summary><see cref="AcTestCheck.Verdict"/> values.</summary>
    public const string TestPassed = "통과";
    public const string TestMismatch = "불일치";
    public const string TestSkipped = "건너뜀";
    public const string TestUnverifiable = "검증 불가";

    /// <summary><c>--set</c> values: the flat S6 cases, the test-first case folders, or both.</summary>
    public const string SetDefault = "default";
    public const string SetTestFirst = "test-first";
    public const string SetAll = "all";

    /// <summary>The approval file of the test-first case folders.</summary>
    public const string ApprovedFile = "APPROVED.json";

    /// <summary>A case folder's config fragment (<c>{ "test": { "command": "…" } }</c>), merged into that case's run options.</summary>
    public const string ConfigFragmentFile = "pelly.json";

    private static readonly JsonSerializerOptions ReadJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The cases of a <c>--set</c>: <see cref="SetDefault"/> reads <paramref name="applyDir"/> only (the folder cases are never
    /// read), <see cref="SetTestFirst"/> <paramref name="testFirstDir"/> only, <see cref="SetAll"/> both (flat cases first).
    /// </summary>
    /// <exception cref="InvalidDataException">A case does not parse, or two cases of <see cref="SetAll"/> share an id.</exception>
    public static IReadOnlyList<ApplyCase> LoadSet(string applyDir, string testFirstDir, string set, string? glob)
    {
        IReadOnlyList<ApplyCase> TestFirst() => LoadTestFirstCases(testFirstDir, glob, LoadApproved(testFirstDir));
        switch (set)
        {
            case SetDefault: return LoadCases(applyDir, glob);
            case SetTestFirst: return TestFirst();
            case SetAll:
                var all = LoadCases(applyDir, glob).Concat(TestFirst()).ToList();
                if (all.GroupBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
                    throw new InvalidDataException($"사례 id가 겹칩니다: {duplicate.Key}");
                return all;
            default: throw new ArgumentException($"알 수 없는 --set: {set}", nameof(set));
        }
    }

    /// <summary>
    /// The case folders in <paramref name="dir"/> (ordinal by folder name) whose id matches <paramref name="glob"/>: each holds
    /// <c>spec.md</c> (the flat cases' front matter rules, id = folder name), optionally <c>files/</c>, <c>expected.json</c>
    /// (<c>[{ "acId", "red", "outcome" }]</c>) and a <see cref="ConfigFragmentFile"/> fragment. A case is approved when
    /// <paramref name="approved"/> lists its id.
    /// </summary>
    /// <exception cref="InvalidDataException">A folder has no spec, a spec does not parse, its id is not the folder name, or expected.json or the fragment is malformed.</exception>
    public static IReadOnlyList<ApplyCase> LoadTestFirstCases(string dir, string? glob, IReadOnlySet<string> approved)
    {
        var cases = new List<ApplyCase>();
        foreach (var folder in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(folder);
            var specFile = Path.Combine(folder, "spec.md");
            if (!File.Exists(specFile)) throw new InvalidDataException($"{id}: spec.md가 없습니다");
            var parsed = Parse($"{id}/spec.md", specFile, id, "폴더 이름");
            if (!TaskFilter.MatchesId(id, glob)) continue;
            var files = Path.Combine(folder, "files");
            cases.Add(parsed with
            {
                FilesDir = Directory.Exists(files) ? files : null,
                Expected = Expected(id, Path.Combine(folder, "expected.json")),
                Approved = approved.Contains(id),
                TestCommand = TestCommandOf(id, Path.Combine(folder, ConfigFragmentFile)),
            });
        }
        return cases;
    }

    /// <summary>
    /// The ids listed in <paramref name="dir"/>'s <see cref="ApprovedFile"/> (<c>{ "approvedBy", "approvedAt", "cases": [ids] }</c>);
    /// empty without the file or its <c>cases</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not that JSON.</exception>
    public static IReadOnlySet<string> LoadApproved(string dir)
    {
        var file = Path.Combine(dir, ApprovedFile);
        if (!File.Exists(file)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var approval = JsonSerializer.Deserialize<Approval>(File.ReadAllText(file), ReadJson);
            return new HashSet<string>(approval?.Cases ?? [], StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{ApprovedFile}: {ex.Message}", ex);
        }
    }

    private sealed record Approval(IReadOnlyList<string>? Cases);

    private static IReadOnlyList<ExpectedCriterion>? Expected(string id, string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var expected = JsonSerializer.Deserialize<List<ExpectedCriterion>>(File.ReadAllText(file), ReadJson)
                ?? throw new InvalidDataException($"{id}/expected.json: 비어 있습니다");
            if (expected.Any(e => e is null || e.AcId is null || e.Red is null || e.Outcome is null))
                throw new InvalidDataException($"{id}/expected.json: 항목마다 acId, red, outcome이 있어야 합니다");
            return expected;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{id}/expected.json: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The <c>test.command</c> of a case's config fragment; null without the file. Only that key is accepted: the fragment
    /// exists for the legacy test command (<c>{trx}</c>), and any other setting would make the case's runs incomparable.
    /// </summary>
    private static string? TestCommandOf(string id, string file)
    {
        if (!File.Exists(file)) return null;
        var name = $"{id}/{ConfigFragmentFile}";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            static JsonElement? Only(JsonElement e, string key, JsonValueKind kind) =>
                e.ValueKind == JsonValueKind.Object && e.EnumerateObject().ToList() is [var p]
                && string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == kind ? p.Value : null;
            if (Only(doc.RootElement, "test", JsonValueKind.Object) is not { } test
                || Only(test, "command", JsonValueKind.String) is not { } command
                || string.IsNullOrWhiteSpace(command.GetString()))
                throw new InvalidDataException($"{name}: test.command만 지정할 수 있습니다");
            return command.GetString();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The options of one case's run: <paramref name="options"/> itself, or a copy whose <c>test</c> section carries the case's
    /// <see cref="ApplyCase.TestCommand"/> (the shared options are never changed).
    /// </summary>
    private static PellyOptions OptionsFor(ApplyCase c, PellyOptions options) => c.TestCommand is not { } command
        ? options
        : new PellyOptions
        {
            Llm = options.Llm, Budget = options.Budget, Sampling = options.Sampling, Retry = options.Retry, Log = options.Log,
            Build = options.Build, Sql = options.Sql,
            Test = new TestOptions { Command = command, Projects = options.Test.Projects, TimeoutSeconds = options.Test.TimeoutSeconds },
        };

    /// <summary>The work copy of one case run: <c>&lt;out&gt;/work/&lt;id&gt;-&lt;repeat&gt;</c>, the corpus in its <c>corpus</c> folder.</summary>
    public static string CaseDir(string outDir, string id, int repeat) => Path.Combine(outDir, "work", $"{id}-{repeat}");

    public static string CorpusOf(string caseDir) => Path.Combine(caseDir, "corpus");

    /// <summary>
    /// The specs in <paramref name="dir"/> (<c>*.md</c> but <c>README.md</c>, ordinal by file name) whose id matches
    /// <paramref name="glob"/> (the <c>--tasks</c> rules).
    /// </summary>
    /// <exception cref="InvalidDataException">A spec does not parse, or its id is not its file name.</exception>
    public static IReadOnlyList<ApplyCase> LoadCases(string dir, string? glob)
    {
        var cases = new List<ApplyCase>();
        var files = Directory.GetFiles(dir, "*.md")
            .Where(f => !string.Equals(Path.GetFileName(f), "README.md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var c = Parse(Path.GetFileName(file), file, Path.GetFileNameWithoutExtension(file), "파일 이름");
            if (TaskFilter.MatchesId(c.Id, glob)) cases.Add(c);
        }
        return cases;
    }

    /// <summary>One spec file as a case; <paramref name="name"/> names it in errors, <paramref name="idSource"/> says where its id must come from.</summary>
    private static ApplyCase Parse(string name, string file, string expectedId, string idSource)
    {
        var text = File.ReadAllText(file).Replace("\r\n", "\n");
        var spec = ChangeSpecParser.Parse(text);
        if (spec.Issues.Count > 0)
            throw new InvalidDataException($"{name}: {string.Join("; ", spec.Issues.Select(i => $"줄 {i.Line}: {i.Message}"))}");
        if (spec.Id != expectedId)
            throw new InvalidDataException($"{name}: 머리말 id({spec.Id})가 {idSource}과 다릅니다");
        return new ApplyCase(expectedId, Scope(text), text, spec.Tasks.Count,
            spec.Tasks.Any(t => t.Kind == TaskKind.Sql), spec.Tasks.Any(t => t.Group is not null));
    }

    /// <summary>The front matter's <c>scope: [A, B]</c> (the parser ignores the key); empty without one.</summary>
    private static IReadOnlyList<string> Scope(string textLf)
    {
        var lines = textLf.Split('\n');
        for (var i = 1; i < lines.Length && lines[i].TrimEnd() != "---"; i++)
        {
            if (!lines[i].StartsWith("scope:", StringComparison.Ordinal)) continue;
            var value = lines[i]["scope:".Length..].Trim().TrimStart('[').TrimEnd(']');
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        return [];
    }

    private static bool InScope(ApplyCase c, string root) => c.Scope.Contains(root, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs one case once: skip check, work copy (corpus, support files, an empty <c>.git</c> marker, the spec), restore of the
    /// Modern solution, the apply with <c>--yes</c>, then the independent rebuild of an exit-0 apply. Cancellation throws
    /// (or ends the apply with exit 2); every other failure of the runner is an <c>error</c> row.
    /// </summary>
    public static async Task<ApplyCaseResult> RunCaseAsync(ApplyCase c, int repeat, ApplyBenchSetup setup, string outDir, CancellationToken ct)
    {
        ApplyCaseResult Skip(string reason)
        {
            setup.Log($"[{c.Id}#{repeat}] 건너뜀: {reason}");
            return new ApplyCaseResult(c.Id, repeat, Skipped, reason, 0, false, c.TaskCount, [], null, 0);
        }
        ApplyCaseResult Error(string message, double seconds = 0)
        {
            setup.Log($"[{c.Id}#{repeat}] 실행기 오류: {message}");
            return new ApplyCaseResult(c.Id, repeat, Errored, null, 4, false, c.TaskCount, [], null, seconds, Error: message);
        }

        if (InScope(c, "Legacy") && !setup.IsWindows()) return Skip(WindowsOnly);
        if ((c.HasSql || InScope(c, "Sql")) && !setup.SqlAvailable) return Skip(NoSql);

        var caseDir = CaseDir(outDir, c.Id, repeat);
        var corpus = CorpusOf(caseDir);
        if (Directory.Exists(caseDir)) return Error($"작업 폴더가 이미 있습니다: {caseDir}");
        string specPath;
        try
        {
            specPath = PrepareWorkCopy(c, setup, caseDir, corpus);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error($"작업 복사본을 만들 수 없습니다: {ex.Message}");
        }

        if (InScope(c, "Modern") && SolutionRestore.Pick(setup.Fs, Path.Combine(corpus, "Modern")) is { } sln)
        {
            var restore = await setup.Processes.RunAsync(SolutionRestore.Spec(sln, Path.GetDirectoryName(sln)!), ct);
            if (restore.ExitCode != 0 || restore.TimedOut)
                return Skip(SolutionRestore.WithTail($"복원 실패 (dotnet restore {SolutionRestore.Head(restore)})", restore));
        }

        var ui = new EvalUserInteraction($"{c.Id}#{repeat}", setup.Log);
        var caseOptions = OptionsFor(c, setup.Options);
        var runner = SpecRunnerFactory.Create(setup.Fs, caseOptions, ui, setup.Time, setup.Processes,
            setup.RunFactory(caseOptions), ImpactFinders.Default(setup.Fs), setup.IsWindows);
        var options = new ApplyOptions(Continue: false, Resume: false, ReviewAtEnd: false, Yes: true, DryRun: false, MaxRetries: null, Verbose: false);

        setup.Log($"[{c.Id}#{repeat}] 실행: 작업 {c.TaskCount}개 → {corpus}");
        var started = setup.Time.GetTimestamp();
        int exit;
        try
        {
            exit = await runner.RunAsync(specPath, options, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message, setup.Time.GetElapsedTime(started).TotalSeconds);
        }
        var seconds = setup.Time.GetElapsedTime(started).TotalSeconds;

        var ws = new Workspace(corpus, Path.Combine(corpus, ".pelly"));
        var record = ApplyHistory.Load(setup.Fs, ws, null).Applies.LastOrDefault()?.Summary;
        var archived = record?.Archived ?? false;

        (bool? ok, string? detail) independent = exit == 0 ? await IndependentBuildAsync(setup, ws, c.FilesDir, ct) : (null, null);
        var criteria = record?.Criteria;
        var checks = exit == 0 && criteria is not null ? await TestJudgeAsync(c, setup, ws, caseDir, criteria, ct) : null;
        var mismatches = checks?.Count(k => k.Verdict == TestMismatch) ?? 0;

        var outcome = exit == 0 && archived ? Completed : Failed;
        setup.Log($"[{c.Id}#{repeat}] {outcome} (종료 {exit}, 보관 {(archived ? "예" : "아니오")}, {seconds:0.0}초"
            + (exit == 0 ? $", 독립 빌드 {(independent.ok switch { true => "통과", false => "위반", null => "검증 불가" })}" : "")
            + (independent.detail is { } d ? $" — {d}" : "")
            + (checks is { Count: > 0 } ? $", 독립 테스트 {string.Join(", ", checks.Select(k => $"{k.AcId} {k.Verdict}" + (k.Detail is { } kd ? $" — {kd}" : "")))}" : "")
            + ")");
        return new ApplyCaseResult(c.Id, repeat, outcome, null, exit, archived, c.TaskCount, record?.Tasks ?? [], independent.ok, seconds,
            IndependentDetail: independent.detail, Criteria: criteria, TestJudgeMismatches: mismatches, TestChecks: checks);
    }

    /// <summary>
    /// Builds <paramref name="caseDir"/>: the support files at its top, the corpus in <c>corpus/</c> (standard excludes), an empty
    /// <c>.git</c> folder there so the whole copy is one workspace (each root has its own <c>.sln</c>), the spec, then the case's
    /// <c>files/</c> over <c>corpus/</c>.
    /// </summary>
    /// <returns>The spec's path, <c>corpus/changes/&lt;id&gt;.md</c>.</returns>
    private static string PrepareWorkCopy(ApplyCase c, ApplyBenchSetup setup, string caseDir, string corpus)
    {
        Directory.CreateDirectory(caseDir);
        foreach (var support in setup.SupportFiles)
            File.Copy(support, Path.Combine(caseDir, Path.GetFileName(support)));
        WorkCopy.CopyDirectory(setup.CorpusDir, corpus);
        Directory.CreateDirectory(Path.Combine(corpus, ".git"));
        var changes = Path.Combine(corpus, "changes");
        Directory.CreateDirectory(changes);
        var specPath = Path.Combine(changes, c.Id + ".md");
        File.WriteAllText(specPath, c.SpecText);
        if (c.FilesDir is { } files) WorkCopy.Overlay(files, corpus);
        return specPath;
    }

    /// <summary>
    /// Rebuilds the projects of the C# files whose bytes now differ from the pristine corpus (pelly's records are not used) and
    /// their dependents; the baseline of a file the case's <c>files/</c> overlays is that file. True: no compiler error; false:
    /// compiler errors (invariant violated); null: nothing to judge or the build could not judge, with the reason.
    /// </summary>
    private static async Task<(bool? Ok, string? Detail)> IndependentBuildAsync(ApplyBenchSetup setup, Workspace ws, string? filesDir, CancellationToken ct)
    {
        try
        {
            var changed = ChangedCsFiles(ws.Root, setup.CorpusDir, filesDir);
            if (changed.Count == 0) return (null, NoCsChange);

            var projects = new List<string>();
            foreach (var file in changed)
            {
                if (ProjectFileLocator.FindNearestCsproj(setup.Fs, file) is not { } csproj)
                    return (null, $"독립 검증 불가: 프로젝트(.csproj)를 찾을 수 없습니다 ({Path.GetRelativePath(ws.Root, file)})");
                if (!projects.Contains(csproj, PathComparers.ForOs)) projects.Add(csproj);
            }

            var scope = ProjectReferenceGraph.AffectedProjects(setup.Fs, ws.Root, projects);
            var build = await setup.Verifier.BuildAsync(ws, scope, ct);
            if (!build.Verifiable) return (null, $"독립 검증 불가: {build.Reason}");
            return build.Errors.Count == 0
                ? (true, null)
                : (false, $"컴파일러 오류 {build.Errors.Count}건: {string.Join("; ", build.Errors.Take(3).Select(e => $"{e.Code} {e.Message}"))}");
        }
        catch (ProjectScopeException ex)
        {
            return (null, $"독립 검증 불가: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"독립 검증 불가: {ex.Message}");
        }
    }

    /// <summary>
    /// The <c>.cs</c> files under <paramref name="workRoot"/> (standard excludes) that are new or differ byte for byte from their
    /// baseline: <paramref name="pristineRoot"/> with <paramref name="overlayRoot"/> (the case's <c>files/</c>) on top.
    /// </summary>
    private static List<string> ChangedCsFiles(string workRoot, string pristineRoot, string? overlayRoot)
    {
        var skip = new HashSet<string>(WorkCopy.Excluded, StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        var pending = new Stack<string>([workRoot]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.GetFiles(dir, "*.cs").Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = Path.GetRelativePath(workRoot, file);
                var pristine = overlayRoot is not null && File.Exists(Path.Combine(overlayRoot, relative))
                    ? Path.Combine(overlayRoot, relative)
                    : Path.Combine(pristineRoot, relative);
                if (!File.Exists(pristine) || !File.ReadAllBytes(pristine).AsSpan().SequenceEqual(File.ReadAllBytes(file)))
                    changed.Add(file);
            }
            foreach (var sub in Directory.GetDirectories(dir))
                if (!skip.Contains(Path.GetFileName(sub))) pending.Push(sub);
        }
        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    /// <summary>
    /// The independent re-run (design V2-2c §5.2): every criterion pelly called <see cref="AcOutcome.Met"/> has its <c>test:</c>
    /// built and run again outside pelly with <see cref="TestRunner"/>, in each project declaring it (<see cref="TestMethodIndex"/>
    /// over the work copy); every result matching the name (<see cref="TestNames.Matches"/>) must pass. Reports go to
    /// <c>&lt;case&gt;/test-judge/</c>, outside the corpus.
    /// </summary>
    private static async Task<IReadOnlyList<AcTestCheck>> TestJudgeAsync(ApplyCase c, ApplyBenchSetup setup, Workspace ws, string caseDir,
        IReadOnlyList<AcResult> criteria, CancellationToken ct)
    {
        var tests = ChangeSpecParser.Parse(c.SpecText).Criteria.Where(a => a.Test is not null)
            .GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Test!, StringComparer.Ordinal);
        var options = OptionsFor(c, setup.Options);
        var runner = new TestRunner(setup.Processes, options.Build, options.Test);
        var tools = new BuildToolResolver(setup.Fs, setup.Processes, options.Build, setup.IsWindows);
        var checks = new List<AcTestCheck>();
        foreach (var ac in criteria.Where(a => a.Outcome == AcOutcome.Met))
        {
            AcTestCheck check;
            if (!tests.TryGetValue(ac.AcId, out var name))
                check = new AcTestCheck(ac.AcId, TestUnverifiable, "명세에 그 수용 기준의 test:가 없습니다");
            else
            {
                try
                {
                    check = await CheckTestAsync(ac.AcId, name, setup, ws, runner, tools, Path.Combine(caseDir, "test-judge"), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    check = new AcTestCheck(ac.AcId, TestUnverifiable, ex.Message);
                }
            }
            checks.Add(check);
        }
        return checks;
    }

    private static async Task<AcTestCheck> CheckTestAsync(string acId, string name, ApplyBenchSetup setup, Workspace ws, TestRunner runner,
        BuildToolResolver tools, string reportDir, CancellationToken ct)
    {
        AcTestCheck Verdict(string verdict, string? detail = null) => new(acId, verdict, detail);

        var decls = TestMethodIndex.Find(setup.Fs, ws.Root, name);
        if (decls.Count == 0) return Verdict(TestMismatch, $"테스트 {name}을(를) 작업 공간에서 찾지 못했습니다");
        if (decls.Any(d => d.Csproj is null)) return Verdict(TestUnverifiable, $"테스트 {name}이(가) 어떤 테스트 프로젝트에도 속하지 않습니다");
        var projects = decls.Select(d => Path.GetFullPath(d.Csproj!)).Distinct(PathComparers.ForOs).ToList();

        // Every declaring project's tool first, so a legacy project off Windows skips the check before anything runs.
        var resolved = new List<BuildTool>();
        foreach (var project in projects)
        {
            var style = ProjectStyleDetector.Detect(setup.Fs, project);
            if (style == ProjectStyle.Legacy && !setup.IsWindows())
                return Verdict(TestSkipped, "레거시 테스트는 Windows에서만 실행합니다");
            var resolution = await tools.ResolveAsync(style, ct);
            if (resolution.Tool is not { } tool) return Verdict(TestUnverifiable, resolution.Error);
            resolved.Add(tool);
        }

        var matched = new List<TestCaseResult>();
        for (var i = 0; i < projects.Count; i++)
        {
            var trx = Path.Combine(reportDir, $"{acId}-{i + 1}.trx");
            var run = await runner.RunAsync(resolved[i], projects[i], ws.Root, trx, ct);
            if (run.TimedOut) return Verdict(TestUnverifiable, "테스트 실행 시간이 초과되었습니다");
            if (run.Error is { } error) return Verdict(TestUnverifiable, error);
            // As in GroupTester: a non-compiler error (restore, SDK) says nothing about the test.
            if (run.BuildErrors.FirstOrDefault(e => !BuildMessages.IsCompilerError(e)) is { } blocker)
                return Verdict(TestUnverifiable, $"테스트 프로젝트를 빌드할 수 없습니다: {blocker.Code} {blocker.Message}");
            if (run.BuildErrors.Count > 0)
                return Verdict(TestMismatch, $"테스트 프로젝트가 빌드되지 않습니다: {string.Join("; ", run.BuildErrors.Take(3).Select(e => $"{e.Code} {e.Message}"))}");
            if (!run.PerTestResults) return Verdict(TestUnverifiable, "테스트별 결과가 없습니다 (레거시는 test.command에 {trx}를 넣으세요)");
            matched.AddRange(run.Results.Where(r => TestNames.Matches(name, r.FullName)));
        }
        if (matched.Count == 0) return Verdict(TestMismatch, $"테스트 결과에 {name}이(가) 없습니다");
        var failed = matched.Where(r => !r.Passed).Select(r => r.FullName).Distinct(StringComparer.Ordinal).ToList();
        return failed.Count == 0 ? Verdict(TestPassed) : Verdict(TestMismatch, $"실패: {string.Join(", ", failed)}");
    }

    /// <summary>
    /// The command's exit code, as <c>run</c>. Precedence: 2 cancelled; 1 an exit-0 apply whose independent build has compiler
    /// errors or whose Met criterion's test does not pass when re-run (the verification invariant outranks a setup problem of another case); 4 any <c>error</c> row (the runner could not
    /// run a case: copy failure, runner exception); else 0.
    /// </summary>
    public static int ExitCode(IReadOnlyList<ApplyCaseResult> results, bool cancelled) =>
        cancelled ? 2
        : results.Any(r => r.IndependentBuildOk == false || r.TestJudgeMismatches > 0) ? 1
        : results.Any(r => r.Outcome == Errored) ? 4
        : 0;
}
