using System.IO.Abstractions;
using PellyCode.Core.Builds;
using PellyCode.Core.CSharp;
using PellyCode.Core.Files;
using PellyCode.Core.Models;
using PellyCode.Core.Specs;
using PellyCode.Core.Testing;
using PellyCode.Core.Verification;
using PellyCode.McpServer.Baseline;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Verify;

/// <summary>
/// The C# part of <c>pelly_verify</c>, root-relative project paths with '/'. <see cref="Changed"/> own the changed files,
/// <see cref="Affected"/> adds the projects that reference them (what the live tree builds), <see cref="BaselineProjects"/> adds
/// the projects an affected test project references, so that its tests are found from them in the baseline and the live tree alike.
/// </summary>
public sealed record CsScope(IReadOnlyList<string> Changed, IReadOnlyList<string> Affected, IReadOnlyList<string> BaselineProjects);

/// <summary>
/// Builds the affected C# projects and runs their test projects in the live tree, then reports what the HEAD baseline did not
/// have (design §7): build errors by file, code and message (lines ignored), tests by <see cref="TestRegressions.Find(TestRun, TestRun, IReadOnlyList{string})"/>.
/// </summary>
/// <param name="checkedElsewhere">A root-relative path the SQL or web check looks at (not mapped to its project's build).</param>
public sealed class CsVerifier(IFileSystem fs, ServerContext ctx, string mirrorDir, IGroupVerifier verifier, IBaselineTestRunner tests,
    Func<string, bool> checkedElsewhere)
{
    public const string NoTestProjects = "{0}를 참조하는 테스트 프로젝트 없음. 동작 변경은 확인되지 않았습니다";
    public const string AllErrorsNote = "HEAD 기준을 만들지 못해 모든 오류를 보입니다: {0}";
    public const string AllTestFailuresNote = "HEAD 기준 테스트를 실행하지 못해 실패한 테스트를 모두 보입니다 (HEAD에서도 실패했을 수 있음): {0}";
    public const string TestsNotRunAfterBuildErrors = "새 빌드 오류가 있어 테스트를 실행하지 않았습니다";

    private static readonly TestRun EmptyRun = new([], [], 0, false, true, null);
    private int _trx;

    /// <summary>
    /// The projects of the changed files (see <see cref="OwningProjects"/>; a deleted or renamed file's project is found from its
    /// old path); null when no changed file belongs to a project. A reference walk that cannot be trusted falls back to the changed
    /// projects, with a note.
    /// </summary>
    public CsScope? Scope(IReadOnlyList<GitChange> changes, VerifyReport report)
    {
        var changed = new List<string>();
        foreach (var c in changes)
            foreach (var rel in new[] { c.Path, c.OldPath }.OfType<string>())
                foreach (var project in OwningProjects(rel))
                    if (!changed.Contains(project, PathComparers.ForOs)) changed.Add(project);
        if (changed.Count == 0) return null;

        IReadOnlyList<string> affected;
        try
        {
            affected = ProjectReferenceGraph.AffectedProjects(fs, ctx.Root, changed);
        }
        catch (Exception e) when (e is ProjectScopeException or IOException or UnauthorizedAccessException)
        {
            report.Note($"참조하는 프로젝트를 모두 찾지 못해 바뀐 프로젝트만 빌드합니다: {e.Message}");
            affected = changed;
        }

        var baseline = affected.ToList();
        foreach (var p in affected.Where(IsTestProject))
            foreach (var r in ProjectReferenceGraph.References(fs, p))
                if (fs.File.Exists(r) && IsUnderRoot(r) && !baseline.Contains(r, PathComparers.ForOs)) baseline.Add(r);

        return new CsScope(changed.Select(Relative).ToList(), affected.Select(Relative).ToList(), baseline.Select(Relative).ToList());
    }

    /// <summary>Builds and tests <paramref name="scope"/> in the live tree and adds what is new against <paramref name="baseline"/>.</summary>
    public async Task CheckAsync(CsScope scope, Baseline.Baseline baseline, VerifyReport report, IProgress<string>? progress, CancellationToken ct)
    {
        var baseBuild = baseline.Build is { Verifiable: true } b ? b : null;
        if (baseline.Status == BaselineStatus.Failed)
            report.Note(string.Format(baseBuild is null ? AllErrorsNote : AllTestFailuresNote, baseline.FailureReason));

        progress?.Report("바뀐 C# 프로젝트를 빌드하는 중입니다");
        var buildHeading = "[빌드] " + string.Join(", ", scope.Affected.Select(Name));
        var live = await verifier.BuildAsync(ctx.Workspace, scope.Affected.Select(Full).ToList(), ct);
        if (!live.Verifiable)
        {
            // HEAD built here, so the change is the likely cause (a broken csproj, a missing package): the model must see it.
            if (baseBuild is not null) report.Error(buildHeading, live.Reason ?? "빌드를 검증할 수 없습니다");
            else report.Skip(buildHeading, live.Reason ?? "빌드를 검증할 수 없습니다");
            report.Skip("[테스트]", "빌드를 검증하지 못해 테스트를 실행하지 않았습니다");
            return;
        }

        var before = (baseBuild?.Errors ?? []).Select(WithRelativePath).ToList();
        var now = live.Errors.Select(WithRelativePath).ToList();
        var fresh = BaselineDiff.New(before, now);
        if (fresh.Count == 0)
        {
            report.Pass(buildHeading);
        }
        else
        {
            foreach (var e in fresh)
                report.Error("[빌드] " + ErrorProject(e, scope), Line(e));
            report.Skip("[테스트]", TestsNotRunAfterBuildErrors);
            return;
        }

        await TestAsync(scope, baseline, report, progress, ct);
    }

    private async Task TestAsync(CsScope scope, Baseline.Baseline baseline, VerifyReport report, IProgress<string>? progress, CancellationToken ct)
    {
        var testProjects = new List<string>(); // root-relative
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        try
        {
            foreach (var p in scope.BaselineProjects)
            {
                var list = TestProjectLocator.Find(fs, ctx.Root, Full(p), ctx.Options.Test).Select(Relative).Distinct(StringComparer.Ordinal).ToList();
                found[p] = list;
                foreach (var t in list.Where(t => !testProjects.Contains(t, StringComparer.Ordinal))) testProjects.Add(t);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Skip("[테스트]", $"테스트 프로젝트를 찾지 못했습니다: {e.Message}");
            return;
        }

        foreach (var p in scope.Changed.Where(p => !IsTestProject(Full(p)) && found.GetValueOrDefault(p) is not { Count: > 0 }))
            report.Skip("[테스트]", string.Format(NoTestProjects, Name(p)));

        foreach (var rel in testProjects)
        {
            ct.ThrowIfCancellationRequested();
            var heading = "[테스트] " + Name(rel);
            progress?.Report($"테스트를 실행하는 중입니다: {Name(rel)}");
            var trx = fs.Path.Combine(ctx.Workspace.PellyDir, "verify", $"test-{Interlocked.Increment(ref _trx)}.trx");
            var run = await tests.RunAsync(Full(rel), ctx.Root, trx, ct);
            var before = baseline.Tests.GetValueOrDefault(rel) ?? EmptyRun;

            if (run.TimedOut)
            {
                report.Error(heading, TestStep.TimedOutMessage(ctx.Options.Test));
                continue;
            }
            IReadOnlyList<TestCaseResult> failures;
            if (run.ReportMissing && before.PerTestResults)
            {
                failures = Regressions(before, run, rel);
            }
            else if (run.Error is { } error)
            {
                report.Skip(heading, $"테스트를 실행하지 못했습니다: {error}");
                continue;
            }
            else if (run.BuildErrors.Count > 0)
            {
                report.Skip(heading, "테스트 프로젝트가 빌드되지 않아 테스트를 실행하지 못했습니다");
                continue;
            }
            else
            {
                // A HEAD test project that did not compile has nothing to compare with: every failure counts.
                failures = before.BuildErrors.Count > 0 ? TestRegressions.Strict(run, []) : Regressions(before, run, rel);
            }

            if (failures.Count == 0) report.Pass(heading);
            foreach (var f in failures.DistinctBy(f => f.FullName))
                report.Error(heading, FirstLine(f.Message) is { Length: > 0 } message ? $"{f.FullName}: {message}" : f.FullName);
        }
    }

    private IReadOnlyList<TestCaseResult> Regressions(TestRun before, TestRun run, string rel)
    {
        var (found, exitOnly) = TestRegressions.Find(before, run, []);
        return exitOnly ? [new TestCaseResult(Name(rel), false, $"종료 코드 {run.ExitCode}로 실패했습니다")] : found;
    }

    /// <summary>
    /// The full paths of the projects a changed root-relative path concerns: a <c>.csproj</c> itself; any other file under a project
    /// folder (nearest <c>.csproj</c> walking up: <c>.cs</c>, <c>.resx</c>, <c>.cshtml</c>, <c>packages.config</c>, a project's
    /// <c>.props</c>…) that project; a build file outside every project folder (<c>Directory.Build.props</c>, <c>*.targets</c>,
    /// <c>NuGet.config</c>…) the projects of the root solution under its folder. Empty for files the SQL or web check looks at
    /// and for files no project builds from.
    /// </summary>
    public IReadOnlyList<string> OwningProjects(string rel)
    {
        if (checkedElsewhere(rel)) return [];
        var full = Full(rel);
        if (rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return fs.File.Exists(full) ? [full] : [];
        var project = ProjectFileLocator.FindNearestCsproj(fs, full);
        if (project is not null && IsUnderRoot(project)) return [fs.Path.GetFullPath(project)];
        return IsBuildFile(rel) ? SolutionProjectsUnder(fs.Path.GetDirectoryName(full)!) : [];
    }

    private static bool IsBuildFile(string rel)
    {
        var name = rel[(rel.LastIndexOf('/') + 1)..];
        return name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || name.Equals("packages.config", StringComparison.OrdinalIgnoreCase) || name.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase)
            || name.Equals("global.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The existing projects of the root's <c>.sln</c>/<c>.slnx</c> files that sit under <paramref name="dir"/>.</summary>
    private List<string> SolutionProjectsUnder(string dir)
    {
        try
        {
            var solutions = fs.Directory.EnumerateFiles(ctx.Root)
                .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal);
            var under = fs.Path.GetFullPath(dir);
            return solutions.SelectMany(sln => SolutionProjects.Read(fs, sln))
                .Where(p => fs.File.Exists(p) && IsUnderRoot(p) && IsUnder(under, p))
                .Distinct(PathComparers.ForOs).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }

    private bool IsUnder(string dir, string full)
    {
        var rel = fs.Path.GetRelativePath(dir, full);
        return !fs.Path.IsPathRooted(rel) && !rel.StartsWith("..", StringComparison.Ordinal);
    }

    private bool IsTestProject(string full)
    {
        if (ctx.Options.Test.Projects.Count == 0)
            return fs.Path.GetFileNameWithoutExtension(full).Contains("Test", StringComparison.OrdinalIgnoreCase);
        return ctx.Options.Test.Projects.Any(p => PathComparers.ForOs.Equals(Full(p.Replace('\\', '/')), full));
    }

    /// <summary>The project heading of a build error: the project owning its file, else the first changed project.</summary>
    private string ErrorProject(Diagnostic e, CsScope scope)
    {
        if (e.File.Length > 0 && !fs.Path.IsPathRooted(e.File) && ProjectFileLocator.FindNearestCsproj(fs, Full(e.File)) is { } p && IsUnderRoot(p))
            return Name(Relative(p));
        return Name(scope.Changed[0]);
    }

    private static string Line(Diagnostic e) =>
        e.File.Length == 0 ? $"{e.Code} {e.Message}" : $"{e.File}{(e.Line > 0 ? $":{e.Line}" : "")} {e.Code} {e.Message}";

    private static string FirstLine(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /// <summary>The diagnostic with its file relative to the mirror or the live root ('/'), whichever holds it most closely, so both sides compare.</summary>
    private Diagnostic WithRelativePath(Diagnostic d)
    {
        if (d.File.Length == 0 || !fs.Path.IsPathRooted(d.File)) return d;
        // Longest folder first: the mirror can sit under the root (e.g. a root that is the home folder).
        foreach (var dir in new[] { ctx.Root, mirrorDir }.SelectMany(Spellings).OrderByDescending(d => d.TrimEnd('/', '\\').Length))
        {
            var rel = fs.Path.GetRelativePath(dir, d.File);
            if (!fs.Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + fs.Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !rel.StartsWith("../", StringComparison.Ordinal))
                return d with { File = rel.Replace('\\', '/') };
        }
        return d;
    }

    /// <summary>A folder and, on macOS, its <c>/private</c> spelling (MSBuild reports /var and /tmp resolved).</summary>
    private static IEnumerable<string> Spellings(string dir)
    {
        yield return dir;
        if (OperatingSystem.IsMacOS() && (dir.StartsWith("/var/", StringComparison.Ordinal) || dir.StartsWith("/tmp/", StringComparison.Ordinal)))
            yield return "/private" + dir;
    }

    private bool IsUnderRoot(string full)
    {
        var rel = fs.Path.GetRelativePath(ctx.Root, fs.Path.GetFullPath(full));
        return !fs.Path.IsPathRooted(rel) && !rel.StartsWith("..", StringComparison.Ordinal);
    }

    private string Full(string rel) => fs.Path.GetFullPath(fs.Path.Combine(ctx.Root, rel.Replace('/', fs.Path.DirectorySeparatorChar)));
    private string Relative(string full) => fs.Path.GetRelativePath(ctx.Root, full).Replace('\\', '/');
    private string Name(string rel) => fs.Path.GetFileNameWithoutExtension(rel);
}
