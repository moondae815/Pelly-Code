using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Eval;

/// <summary>One benchmark spec (<c>eval/benchmark/apply/&lt;id&gt;.md</c>): its front matter scope and the shape of its tasks.</summary>
/// <param name="Scope">The corpus roots the spec touches (<c>Modern</c>, <c>Legacy</c>, <c>Sql</c>), as the S6 gold lists them.</param>
/// <param name="SpecText">The spec as written to the work copy's <c>changes/&lt;id&gt;.md</c>.</param>
public sealed record ApplyCase(string Id, IReadOnlyList<string> Scope, string SpecText, int TaskCount, bool HasSql, bool HasGroup);

/// <summary>
/// One case run. <paramref name="Outcome"/>: <c>completed</c> (exit 0 and archived), <c>failed</c>, <c>skipped</c> (never run,
/// <paramref name="SkipReason"/> says why) or <c>error</c> (the runner itself failed, <paramref name="Error"/>).
/// <paramref name="Archived"/> and <paramref name="Tasks"/> come from the apply's own <c>apply.json</c>.
/// <paramref name="IndependentBuildOk"/> is the eval's own rebuild of an exit-0 apply: false is a verification invariant
/// violation, null means not judged (<paramref name="IndependentDetail"/> says why).
/// </summary>
public sealed record ApplyCaseResult(string Id, int Repeat, string Outcome, string? SkipReason,
    int ExitCode, bool Archived, int TaskCount, IReadOnlyList<ApplyTaskResult> Tasks, bool? IndependentBuildOk, double Seconds,
    string? Error = null, string? IndependentDetail = null);

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
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            var spec = ChangeSpecParser.Parse(text);
            if (spec.Issues.Count > 0)
                throw new InvalidDataException($"{name}: {string.Join("; ", spec.Issues.Select(i => $"줄 {i.Line}: {i.Message}"))}");
            if (spec.Id != Path.GetFileNameWithoutExtension(file))
                throw new InvalidDataException($"{name}: 머리말 id({spec.Id})가 파일 이름과 다릅니다");
            if (!TaskFilter.MatchesId(spec.Id, glob)) continue;
            cases.Add(new ApplyCase(spec.Id, Scope(text), text, spec.Tasks.Count,
                spec.Tasks.Any(t => t.Kind == TaskKind.Sql), spec.Tasks.Any(t => t.Group is not null)));
        }
        return cases;
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
        var runner = SpecRunnerFactory.Create(setup.Fs, setup.Options, ui, setup.Time, setup.Processes,
            setup.RunFactory(setup.Options), ImpactFinders.Default(setup.Fs), setup.IsWindows);
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

        (bool? ok, string? detail) independent = exit == 0 ? await IndependentBuildAsync(setup, ws, ct) : (null, null);

        var outcome = exit == 0 && archived ? Completed : Failed;
        setup.Log($"[{c.Id}#{repeat}] {outcome} (종료 {exit}, 보관 {(archived ? "예" : "아니오")}, {seconds:0.0}초"
            + (exit == 0 ? $", 독립 빌드 {(independent.ok switch { true => "통과", false => "위반", null => "검증 불가" })}" : "")
            + (independent.detail is { } d ? $" — {d}" : "") + ")");
        return new ApplyCaseResult(c.Id, repeat, outcome, null, exit, archived, c.TaskCount, record?.Tasks ?? [], independent.ok, seconds,
            IndependentDetail: independent.detail);
    }

    /// <summary>
    /// Builds <paramref name="caseDir"/>: the support files at its top, the corpus in <c>corpus/</c> (standard excludes), an empty
    /// <c>.git</c> folder there so the whole copy is one workspace (each root has its own <c>.sln</c>), and the spec.
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
        return specPath;
    }

    /// <summary>
    /// Rebuilds the projects of the C# files whose bytes now differ from the pristine corpus (pelly's records are not used) and
    /// their dependents. True: no compiler error; false: compiler errors (invariant violated); null: nothing to judge or the
    /// build could not judge, with the reason.
    /// </summary>
    private static async Task<(bool? Ok, string? Detail)> IndependentBuildAsync(ApplyBenchSetup setup, Workspace ws, CancellationToken ct)
    {
        try
        {
            var changed = ChangedCsFiles(ws.Root, setup.CorpusDir);
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

    /// <summary>The <c>.cs</c> files under <paramref name="workRoot"/> (standard excludes) that are new or differ byte for byte from <paramref name="pristineRoot"/>.</summary>
    private static List<string> ChangedCsFiles(string workRoot, string pristineRoot)
    {
        var skip = new HashSet<string>(WorkCopy.Excluded, StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        var pending = new Stack<string>([workRoot]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.GetFiles(dir, "*.cs").Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var pristine = Path.Combine(pristineRoot, Path.GetRelativePath(workRoot, file));
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
    /// The command's exit code, as <c>run</c>. Precedence: 2 cancelled; 1 an exit-0 apply whose independent build has compiler
    /// errors (the verification invariant outranks a setup problem of another case); 4 any <c>error</c> row (the runner could not
    /// run a case: copy failure, runner exception); else 0.
    /// </summary>
    public static int ExitCode(IReadOnlyList<ApplyCaseResult> results, bool cancelled) =>
        cancelled ? 2
        : results.Any(r => r.IndependentBuildOk == false) ? 1
        : results.Any(r => r.Outcome == Errored) ? 4
        : 0;
}
