using System.IO.Abstractions;

namespace PellyCoder.Eval;

/// <summary>A selected task of a run: resolved, or the Definition error that keeps it from running.</summary>
public sealed record PlannedTask(BenchmarkTask Task, SuiteTask? Resolved, string? Error);

/// <param name="LoadErrors">Task files that could not be read, or ids used by more than one file; <c>run</c> stops with exit 4 when there is any.</param>
public sealed record RunPlanResult(IReadOnlyList<PlannedTask> Tasks, IReadOnlyList<string> LoadErrors);

/// <summary>
/// What <c>run</c> decides before any LLM call. It applies the same definition rules as <c>validate</c> so a field suite
/// never crashes the runner: a task whose schema is invalid (for example an id that is not <c>cs-modern-NNN</c>,
/// <c>cs-legacy-NNN</c> or <c>sql-NNN</c>, which could carry a business name into the results) or whose target cannot be
/// resolved is recorded as a Definition failure and never run.
/// </summary>
public static class RunPlan
{
    public static RunPlanResult Prepare(BenchmarkSuite suite, IFileSystem fs, string? tasks, string? kind)
    {
        var (loaded, errors) = BenchmarkTask.TryLoadAll(suite.TasksDir);
        if (errors.Count > 0) return new([], errors);
        // Results are aggregated per id, so two task files with one id would silently keep only one row.
        var duplicates = loaded.Where(t => t.Id.Length > 0).GroupBy(t => t.Id, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => $"중복된 작업 id: {g.Key} ({string.Join(", ", g.Select(t => t.SourceName + ".yaml").Order(StringComparer.Ordinal))})")
            .ToList();
        if (duplicates.Count > 0) return new([], duplicates);
        return new([.. loaded.Where(t => TaskFilter.Matches(t, tasks, kind)).Select(t => Plan(suite, fs, t))], []);
    }

    private static PlannedTask Plan(BenchmarkSuite suite, IFileSystem fs, BenchmarkTask task)
    {
        var schema = SuiteValidator.SchemaIssues(task, task.SourceName).Where(i => i.Level == IssueLevel.Error).ToList();
        if (schema.Count > 0)
        {
            var label = task.Id.Length > 0 ? task.Id : $"{task.SourceName}.yaml";
            return new(task, null, $"{label}: 작업 정의 오류(validate로 확인하세요): {string.Join("; ", schema.Select(i => i.Message))}");
        }
        var resolved = SuiteValidator.TryResolve(suite, task, fs, out var error);
        return new(task, resolved, resolved is null ? error : null);
    }

    /// <summary>
    /// Null when <paramref name="outDir"/> is missing or empty. Otherwise the refusal: a file or unreadable folder, or an earlier
    /// result — writing into it would turn every task whose work folder is left there into a Skipped row (and mix two runs).
    /// </summary>
    public static string? OutConflict(string outDir) => OutputFolder.Problem(outDir,
        $"--out 폴더가 비어 있지 않습니다: {outDir} — 이전 결과에 이어 쓰면 남은 작업 폴더의 작업이 「생략」으로 집계됩니다. 새 폴더를 지정하세요");
}
