using System.IO.Abstractions;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;

namespace PellyCoder.Eval;

public enum IssueLevel { Error, Warning }

/// <param name="TaskId">Null for problems of the suite itself (a YAML file that cannot be read, an empty category, a root).</param>
public sealed record SuiteIssue(IssueLevel Level, string? TaskId, string Message);

/// <summary>
/// Checks a suite's task definitions before any LLM or SQL run (<c>validate</c>): a task that cannot be judged or cannot
/// find its target would otherwise fail one by one in the middle of a long run. Never connects to anything.
/// </summary>
public static partial class SuiteValidator
{
    private static readonly string[] BuildInputs = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.config"];
    private static readonly string[] Categories = ["cs-legacy", "cs-modern", "sql"];
    private static readonly string[] SkippedFolders = [.. WorkCopy.Excluded, "packages"];

    /// <summary>The MSBuild property reference a judge project must build the work copy through (case ignored, as MSBuild does).</summary>
    private const string JudgeTargetRootReference = "$(" + IndependentVerifier.JudgeTargetRootProperty + ")";

    /// <summary>Definition rules that need no file (id, kind, judge). Doc templates (<c>--template</c>) are checked with this too.</summary>
    public static IReadOnlyList<SuiteIssue> SchemaIssues(BenchmarkTask task, string? fileName)
    {
        var issues = new List<SuiteIssue>();
        var id = task.Id ?? "";
        void Error(string message) => issues.Add(new(IssueLevel.Error, id.Length == 0 ? null : id, message));

        if (!TaskId().IsMatch(id)) Error($"id 형식이 올바르지 않습니다 (cs-modern-NNN, cs-legacy-NNN, sql-NNN): '{id}'");
        if (fileName is not null && fileName != id) Error($"id가 파일 이름과 다릅니다: id '{id}', 파일 이름 '{fileName}'");
        if (string.IsNullOrWhiteSpace(task.Request)) Error("request가 비어 있습니다");
        if (task.Judge is not { } judge)
        {
            Error("judge가 없습니다");
            return issues;
        }

        switch (task.Kind)
        {
            case "cs":
                if (!id.StartsWith("cs-", StringComparison.Ordinal)) Error("kind cs 작업의 id는 cs-로 시작해야 합니다");
                if ((task.Method is not null) == (task.Line is not null)) Error("cs 작업은 method와 line 중 정확히 하나만 지정해야 합니다");
                if (task.Object is not null) Error("cs 작업에는 object를 쓸 수 없습니다");
                if (judge.Build != true) Error("cs 작업은 judge.build: true가 필요합니다");
                if (judge.SqlCompile is not null) Error("cs 작업에는 judge.sqlCompile을 쓸 수 없습니다");
                break;
            case "sql":
                if (task.Method is not null) Error("sql 작업에는 method를 쓸 수 없습니다");
                if (!id.StartsWith("sql-", StringComparison.Ordinal)) Error("kind sql 작업의 id는 sql-로 시작해야 합니다");
                if (judge.SqlCompile != true) Error("sql 작업은 judge.sqlCompile: true가 필요합니다");
                if (judge.Build is not null) Error("sql 작업에는 judge.build를 쓸 수 없습니다");
                break;
            default:
                Error($"알 수 없는 kind '{task.Kind}' (cs 또는 sql)");
                break;
        }
        return issues;
    }

    /// <param name="compatLevel">The run settings' <c>sql.compatLevel</c> (parses the SQL targets the way the runner does).</param>
    public static IReadOnlyList<SuiteIssue> Validate(BenchmarkSuite suite, IReadOnlyList<BenchmarkTask> tasks,
        IReadOnlyList<string> loadErrors, IFileSystem fs, int compatLevel)
    {
        var issues = new List<SuiteIssue>();
        issues.AddRange(loadErrors.Select(e => new SuiteIssue(IssueLevel.Error, null, e)));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sdkRoots = new List<SuiteRoot>();
        foreach (var task in tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var schema = SchemaIssues(task, task.SourceName);
            issues.AddRange(schema);
            if (!seen.Add(task.Id))
                issues.Add(new(IssueLevel.Error, task.Id, $"id가 중복됩니다: {task.Id}"));
            if (task.Judge is null || schema.Any(i => i.Level == IssueLevel.Error && i.Message.StartsWith("알 수 없는 kind", StringComparison.Ordinal)))
                continue;
            issues.AddRange(CheckFiles(suite, task, fs, compatLevel, out var resolved));
            if (resolved is { IsLegacy: false } && task.Kind == "cs" && !sdkRoots.Contains(resolved.Root)) sdkRoots.Add(resolved.Root);
        }

        foreach (var category in Categories)
            if (!tasks.Any(t => (t.Id ?? "").StartsWith(category + "-", StringComparison.Ordinal)))
                issues.Add(new(IssueLevel.Warning, null, $"작업이 0건인 범주: {category}"));

        issues.AddRange(CheckRoots(suite, fs));
        issues.AddRange(CheckSolutions(sdkRoots, fs));
        issues.AddRange(CheckJudgeProjects(suite, fs));
        return issues;
    }

    /// <summary>
    /// <see cref="BenchmarkSuite.Resolve"/> that never throws: a target csproj that cannot be read (not XML, locked, no permission)
    /// becomes <paramref name="error"/>. <c>validate</c> reports it, <c>run</c> records the task as a Definition failure.
    /// </summary>
    public static SuiteTask? TryResolve(BenchmarkSuite suite, BenchmarkTask task, IFileSystem fs, out string? error)
    {
        try
        {
            var resolved = suite.Resolve(task, fs, out error);
            if (resolved is null) error ??= $"{task.Id}: 대상 파일을 해석하지 못함";
            return resolved;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            error = $"{task.Id}: 대상 프로젝트 파일을 읽을 수 없음: {ex.Message}";
            return null;
        }
    }

    /// <summary>Whether <paramref name="id"/> has the only accepted form (no business or system names can hide in it).</summary>
    public static bool IsValidId(string? id) => id is not null && TaskId().IsMatch(id);

    /// <summary>SDK-style C# roots: the runner restores the first top-level <c>*.sln</c>; none or several is likely a wrong root.</summary>
    private static List<SuiteIssue> CheckSolutions(IEnumerable<SuiteRoot> sdkRoots, IFileSystem fs)
    {
        var issues = new List<SuiteIssue>();
        foreach (var root in sdkRoots)
        {
            var solutions = SolutionRestore.Solutions(fs, root.Path);
            if (solutions.Count == 0)
                issues.Add(new(IssueLevel.Warning, null, $"루트 {root.Name}: 맨 위에 .sln 파일이 없습니다 — 복원하지 않고 빌드해 실패할 수 있습니다 (모던 루트는 복원할 솔루션이 있는 폴더로 지정하세요)"));
            else if (solutions.Count > 1)
                issues.Add(new(IssueLevel.Warning, null, $"루트 {root.Name}: 맨 위에 .sln 파일이 {solutions.Count}개 있습니다 — 이름순 첫 파일 {fs.Path.GetFileName(solutions[0])}만 복원합니다"));
        }
        return issues;
    }

    /// <summary>Every <c>judges/**/*.csproj</c> of the suite against the judge contract (<see cref="JudgeProjectIssues"/>).</summary>
    private static List<SuiteIssue> CheckJudgeProjects(BenchmarkSuite suite, IFileSystem fs)
    {
        var dir = fs.Path.Combine(suite.Dir, "judges");
        if (!fs.Directory.Exists(dir)) return [];
        string[] projects;
        try
        {
            projects = fs.Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !fs.Path.GetRelativePath(dir, p).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
                .Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new(IssueLevel.Warning, null, $"판정 프로젝트 폴더를 읽을 수 없음: {dir}: {ex.Message}")];
        }
        return [.. projects.SelectMany(p => JudgeProjectIssues(fs, p))];
    }

    /// <summary>
    /// The judge contract: the runner copies a judge project next to the work copy and builds it with
    /// <c>-p:JudgeTargetRoot=&lt;work copy of the root&gt;</c>. A <c>ProjectReference</c> that does not go through
    /// <c>$(JudgeTargetRoot)</c> builds and tests the original sources (writing <c>bin</c>/<c>obj</c> there) instead of the edit;
    /// a project that never uses the property does not see the edit at all. Warnings: the judge then fails silently, not loudly.
    /// </summary>
    public static IReadOnlyList<SuiteIssue> JudgeProjectIssues(IFileSystem fs, string csproj)
    {
        string text;
        XDocument doc;
        try
        {
            text = fs.File.ReadAllText(csproj);
            doc = XDocument.Parse(text);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return [new(IssueLevel.Warning, null, $"판정 프로젝트를 읽을 수 없음: {csproj}: {ex.Message}")];
        }

        var issues = new List<SuiteIssue>();
        foreach (var reference in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = (string?)reference.Attribute("Include") ?? "";
            if (!include.Contains(JudgeTargetRootReference, StringComparison.OrdinalIgnoreCase))
                issues.Add(new(IssueLevel.Warning, null, $"판정 프로젝트 {csproj}: ProjectReference '{include}'가 {JudgeTargetRootReference}를 거치지 않습니다 — "
                    + "수정본이 아니라 원본을 빌드·시험합니다(원본 폴더에 bin/obj가 생김)"));
        }
        // Attribute values and element text only: a comment that names the property (the template has one) is not a use of it.
        var values = doc.Descendants().SelectMany(e => e.Attributes().Select(a => a.Value)
            .Concat(e.Nodes().OfType<XText>().Select(t => t.Value))).ToList();
        var placeholders = values.SelectMany(v => TemplatePlaceholder().Matches(v).Select(m => m.Value)).Distinct(StringComparer.Ordinal).ToList();
        if (placeholders.Count > 0)
            issues.Add(new(IssueLevel.Warning, null, $"판정 프로젝트 {csproj}: 템플릿 자리 표시가 남아 있습니다: {string.Join(", ", placeholders)}"));
        if (!values.Any(v => v.Contains(JudgeTargetRootReference, StringComparison.OrdinalIgnoreCase)))
            issues.Add(new(IssueLevel.Warning, null, $"판정 프로젝트 {csproj}: {JudgeTargetRootReference}를 쓰지 않습니다 — 수정본을 시험하지 않습니다 (ProjectReference 또는 Compile을 {JudgeTargetRootReference} 아래로 지정하세요)"));
        return issues;
    }

    private static List<SuiteIssue> CheckFiles(BenchmarkSuite suite, BenchmarkTask task, IFileSystem fs, int compatLevel, out SuiteTask? resolved)
    {
        var issues = new List<SuiteIssue>();
        void Error(string message) => issues.Add(new(IssueLevel.Error, task.Id, message));

        resolved = TryResolve(suite, task, fs, out var error);
        if (resolved is null)
        {
            Error(error!);
            return issues;
        }

        var judge = task.Judge!;
        if (task.Kind == "cs")
        {
            var csproj = ProjectFileLocator.FindNearestCsproj(fs, resolved.SourceFile);
            if (csproj is null) Error($"csproj를 찾지 못함: {resolved.SourceFile} 위의 어느 폴더에도 없습니다");
            else if (task.Id.StartsWith("cs-legacy-", StringComparison.Ordinal) && !resolved.IsLegacy)
                Error($"cs-legacy 작업인데 대상 프로젝트가 SDK 형식입니다: {csproj}");
            else if (task.Id.StartsWith("cs-modern-", StringComparison.Ordinal) && resolved.IsLegacy)
                Error($"cs-modern 작업인데 대상 프로젝트가 Legacy 형식입니다: {csproj}");
            if (resolved.IsLegacy && ((judge.Tests?.Count ?? 0) > 0 || (judge.FailingTests?.Count ?? 0) > 0))
                Error("Legacy 작업에는 동작 판정을 둘 수 없음 (Windows에서도 생략됨)");
        }

        if ((judge.MustContain?.Count ?? 0) + (judge.MustAdd?.Count ?? 0) + (judge.MustNotContain?.Count ?? 0)
            + (judge.Tests?.Count ?? 0) + (judge.FailingTests?.Count ?? 0) == 0)
            Error("내용 판정(mustContain·mustAdd·mustNotContain)이나 동작 판정(tests·failingTests)이 하나도 없습니다 — 아무 수정이나 통과합니다");

        string text;
        try
        {
            text = TextFileCodec.Read(fs, resolved.SourceFile).Text.TrimStart('﻿').Replace("\r\n", "\n");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Error($"대상 파일을 읽을 수 없음: {ex.Message}");
            return issues;
        }

        if (task.Line is { } line && (line < 1 || line > text.Split('\n').Length))
            Error($"line {line}이 파일 줄 수 밖입니다 (1~{text.Split('\n').Length})");

        string? scope = null;
        if (task.Kind == "sql")
        {
            var (script, batches) = TargetText.SqlCandidates(text, task.Object, compatLevel);
            var fileErrors = script.Batches.Sum(b => b.ParseErrors.Count);
            if (batches.Count != 1)
                Error($"대상 객체 배치 {batches.Count}개 — 정확히 1개여야 합니다{(task.Object is null && batches.Count > 1 ? " (object를 지정하세요)" : "")}"
                    + (batches.Count == 0 && fileErrors > 0 ? $" (파일에 구문 오류 {fileErrors}개 — 구문 오류가 있는 배치는 객체로 인식되지 않습니다)" : ""));
            else if (batches[0].ParseErrors.Count > 0) Error($"원본 대상 배치에 구문 오류 {batches[0].ParseErrors.Count}개");
            else scope = batches[0].Text;
        }
        else if (task.Line is null || (task.Line >= 1 && task.Line <= text.Split('\n').Length))
        {
            scope = TargetText.ForCs(text, task.Method, task.Line);
            if (scope is null) Error("원본에서 대상을 찾지 못함 (method가 없거나 여러 개이거나, line 둘레에 멤버가 없음)");
        }

        if (scope is null) return issues;
        foreach (var s in judge.MustContain ?? [])
            if (scope.Contains(s, StringComparison.Ordinal))
                Error($"mustContain '{s}'가 원본 대상에 이미 있습니다 — 아무것도 고치지 않아도 통과합니다");
        foreach (var s in judge.MustNotContain ?? [])
            if (!scope.Contains(s, StringComparison.Ordinal))
                Error($"mustNotContain '{s}'가 원본 대상에 없습니다 — 아무것도 고치지 않아도 통과합니다");
        return issues;
    }

    /// <summary>
    /// Deep check (<c>validate --deep</c>): runs every C# task's judge tests on a scratch copy of the original. <c>tests</c> must
    /// fail there (a test that already passes accepts any patch), <c>failingTests</c> must pass (they pin the behavior before the fix).
    /// A tooling problem (restore, mirror, test host) is a warning, not a definition error. Scratch copies are deleted after each task.
    /// </summary>
    public static async Task<IReadOnlyList<SuiteIssue>> ValidateJudgesAsync(BenchmarkSuite suite, IReadOnlyList<SuiteTask> tasks,
        Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>, string, CancellationToken, Task<JudgeTestRun>> runJudges,
        IProcessRunner processes, string scratchDir, CancellationToken ct)
    {
        var issues = new List<SuiteIssue>();
        var targets = tasks.Where(t => t.Task.Kind == "cs" && !t.IsLegacy
                && ((t.Task.Judge?.Tests?.Count ?? 0) > 0 || (t.Task.Judge?.FailingTests?.Count ?? 0) > 0))
            .OrderBy(t => t.Task.Id, StringComparer.Ordinal).ToList();
        if (targets.Count == 0) return issues;

        var createdScratch = !Directory.Exists(scratchDir);
        try
        {
            foreach (var task in targets)
            {
                var id = task.Task.Id;
                var work = Path.Combine(scratchDir, id);
                try
                {
                    WorkCopy.Prepare(suite, task, work);
                    var root = Path.Combine(work, task.Root.Name);
                    if (SolutionRestore.Pick(new FileSystem(), root) is { } sln) // the runner's choice (one shared rule)
                    {
                        var restore = await processes.RunAsync(SolutionRestore.Spec(sln, root), ct);
                        if (restore.ExitCode != 0 || restore.TimedOut)
                        {
                            issues.Add(new(IssueLevel.Warning, id, SolutionRestore.WithTail($"원본 복원에 실패해 판정 테스트를 확인하지 못함 ({SolutionRestore.Head(restore)})", restore)));
                            continue;
                        }
                    }

                    var tests = task.Task.Judge!.Tests ?? [];
                    var failing = task.Task.Judge.FailingTests ?? [];
                    var run = await runJudges(tests, failing, root, ct);
                    if (run.Error is not null)
                    {
                        issues.Add(new(run.ToolingError ? IssueLevel.Warning : IssueLevel.Error, id,
                            run.ToolingError ? $"판정 도구 오류로 확인하지 못함: {run.Error}" : run.Error));
                        continue;
                    }
                    foreach (var name in tests)
                        if (!run.Results.TryGetValue(name, out var r)) issues.Add(new(IssueLevel.Error, id, $"결과에 없음: {name}"));
                        else if (r.Passed) issues.Add(new(IssueLevel.Error, id, $"판정 테스트가 원본에서 이미 통과: {name} (빈 판정)"));
                    foreach (var name in failing)
                        if (!run.Results.TryGetValue(name, out var r)) issues.Add(new(IssueLevel.Error, id, $"결과에 없음: {name}"));
                        else if (!r.Passed) issues.Add(new(IssueLevel.Error, id, $"수정 전 동작 테스트가 원본에서 실패: {name}"));
                }
                finally
                {
                    DeleteQuietly(work);
                }
            }
        }
        finally
        {
            if (createdScratch && Directory.Exists(scratchDir) && !Directory.EnumerateFileSystemEntries(scratchDir).Any())
                DeleteQuietly(scratchDir);
        }
        return issues;
    }

    private static void DeleteQuietly(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: MSBuild node reuse may still hold files */ }
    }

    private static List<SuiteIssue> CheckRoots(BenchmarkSuite suite, IFileSystem fs)
    {
        var issues = new List<SuiteIssue>();
        var supported = suite.SupportFiles.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in suite.Roots)
        {
            // 작업 복사본은 루트만 담는다: 루트 위 폴더의 빌드 입력은 supportFiles로 넣지 않으면 없다.
            // 스위트 폴더와의 공통 조상에서 멈추지 않고 파일 시스템 루트까지 올라간다.
            for (var dir = fs.Path.GetDirectoryName(root.Path); !string.IsNullOrEmpty(dir); dir = fs.Path.GetDirectoryName(dir))
            {
                if (!fs.Directory.Exists(dir)) continue;
                foreach (var file in SafeFiles(fs, dir))
                {
                    var name = fs.Path.GetFileName(file);
                    if (BuildInputs.Contains(name, StringComparer.OrdinalIgnoreCase) && !supported.Contains(name) && reported.Add(file))
                        issues.Add(new(IssueLevel.Warning, null, $"루트 밖 빌드 입력: {file} — supportFiles에 넣지 않으면 작업 복사본에 없습니다"));
                }
            }

            var skip = new HashSet<string>(SkippedFolders.Concat(root.Exclude), StringComparer.OrdinalIgnoreCase);
            var hasPackagesFolder = fs.Directory.Exists(fs.Path.Combine(root.Path, "packages"));
            if (!hasPackagesFolder && FindPackagesConfig(fs, root.Path, skip))
                issues.Add(new(IssueLevel.Warning, null, $"루트 {root.Name}: packages.config가 있지만 packages/ 폴더가 없습니다 (폐쇄망에서 복원할 수 없음)"));
        }
        return issues;
    }

    private static bool FindPackagesConfig(IFileSystem fs, string dir, HashSet<string> skip)
    {
        if (SafeFiles(fs, dir).Any(f => string.Equals(fs.Path.GetFileName(f), "packages.config", StringComparison.OrdinalIgnoreCase)))
            return true;
        try
        {
            return fs.Directory.GetDirectories(dir).Where(d => !skip.Contains(fs.Path.GetFileName(d))).Any(d => FindPackagesConfig(fs, d, skip));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeFiles(IFileSystem fs, string dir)
    {
        try
        {
            return fs.Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A template placeholder such as <c>{대상 프로젝트}</c>: braces around text with a non-ASCII (Korean) character, so GUIDs in braces are not matched.</summary>
    [GeneratedRegex(@"\{[^{}\r\n]*[^\x00-\x7F][^{}\r\n]*\}")]
    private static partial Regex TemplatePlaceholder();

    [GeneratedRegex(@"\A(cs-(modern|legacy)|sql)-[0-9]{3}\z")]
    private static partial Regex TaskId();
}
