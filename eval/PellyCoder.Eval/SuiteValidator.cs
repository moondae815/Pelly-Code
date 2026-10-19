using System.IO.Abstractions;
using System.Text.RegularExpressions;
using System.Xml;
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
    private static readonly string[] SkippedFolders = ["bin", "obj", ".pelly", "TestResults", ".git", ".vs", "packages"];

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
        foreach (var task in tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var schema = SchemaIssues(task, task.SourceName);
            issues.AddRange(schema);
            if (!seen.Add(task.Id))
                issues.Add(new(IssueLevel.Error, task.Id, $"id가 중복됩니다: {task.Id}"));
            if (task.Judge is null || schema.Any(i => i.Level == IssueLevel.Error && i.Message.StartsWith("알 수 없는 kind", StringComparison.Ordinal)))
                continue;
            issues.AddRange(CheckFiles(suite, task, fs, compatLevel));
        }

        foreach (var category in Categories)
            if (!tasks.Any(t => (t.Id ?? "").StartsWith(category + "-", StringComparison.Ordinal)))
                issues.Add(new(IssueLevel.Warning, null, $"작업이 0건인 범주: {category}"));

        issues.AddRange(CheckRoots(suite, fs));
        return issues;
    }

    private static List<SuiteIssue> CheckFiles(BenchmarkSuite suite, BenchmarkTask task, IFileSystem fs, int compatLevel)
    {
        var issues = new List<SuiteIssue>();
        void Error(string message) => issues.Add(new(IssueLevel.Error, task.Id, message));

        SuiteTask? resolved;
        try
        {
            resolved = suite.Resolve(task, fs, out var error);
            if (resolved is null)
            {
                Error(error ?? "대상 파일을 해석하지 못함");
                return issues;
            }
        }
        catch (XmlException ex)
        {
            Error($"대상 프로젝트 파일을 읽을 수 없음: {ex.Message}");
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
                    var sln = Directory.GetFiles(root, "*.sln").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
                    if (sln is not null)
                    {
                        var restore = await processes.RunAsync(new ProcessSpec("dotnet", ["restore", sln, "--nologo", "--disable-parallel"], root, TimeSpan.FromMinutes(5)), ct);
                        if (restore.ExitCode != 0)
                        {
                            issues.Add(new(IssueLevel.Warning, id, $"원본 복원에 실패해 판정 테스트를 확인하지 못함 (종료 {restore.ExitCode})"));
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

    [GeneratedRegex(@"\A(cs-(modern|legacy)|sql)-[0-9]{3}\z")]
    private static partial Regex TaskId();
}
