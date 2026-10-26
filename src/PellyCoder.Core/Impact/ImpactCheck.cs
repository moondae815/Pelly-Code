using System.IO.Abstractions;
using PellyCoder.Core.Files;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Impact;

/// <summary>
/// Turns a change spec into impact seeds and an impact result into spec warnings (design §5 "영향 탐색"). It only ever
/// produces warnings: a missing impact target is something for the author to look at, not a reason to refuse the spec.
/// </summary>
public static class ImpactCheck
{
    /// <summary>
    /// The not-done cs tasks the search starts from: the grouped ones, or every one when <paramref name="allCsTasks"/>.
    /// sql tasks are never seeds in V2-1c.
    /// </summary>
    public static IReadOnlyList<ImpactSeed> Seeds(ChangeSpec spec, bool allCsTasks) =>
        spec.Tasks
            .Where(t => t is { Kind: TaskKind.CSharp, Done: false, Symbol: not null } && (allCsTasks || t.Group is not null))
            .Select(t =>
            {
                var (className, member) = SpecSymbol.Split(t.Symbol!);
                return new ImpactSeed(t.Id, t.Line, Normalize(t.Path), className, member, t.Kind);
            })
            .ToList();

    /// <summary>Warnings for one impact result: how it was found, unsupported seeds, then each target no task names.</summary>
    public static IReadOnlyList<SpecIssue> Issues(ChangeSpec spec, IReadOnlyList<ImpactSeed> seeds, ImpactResult result)
    {
        var issues = new List<SpecIssue>();
        if (result.Mode == ImpactMode.Name)
            issues.Add(Warning(0, "ImpactNameBased", $"영향 탐색: 이름 기준(부정확): 의미 분석 실패 — {result.Fallback?.Display ?? "알 수 없음"}"));
        if (result.Incomplete)
            issues.Add(Warning(0, "ImpactIncomplete", "영향 탐색: 일부 폴더·파일을 읽지 못해 결과가 불완전합니다"));

        var lines = seeds.GroupBy(s => s.TaskId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Line, StringComparer.Ordinal);
        int LineOf(string taskId) => lines.GetValueOrDefault(taskId);

        foreach (var id in result.UnsupportedSeeds)
            issues.Add(Warning(LineOf(id), "ImpactUnsupported", $"작업 {id}: 영향 탐색은 메서드·속성만 봅니다."));

        foreach (var id in result.SeedsNotFound)
        {
            var seed = seeds.FirstOrDefault(s => s.TaskId == id);
            var where = seed is null ? "" : $": {seed.Path}#{(seed.ClassName is null ? seed.Member : seed.ClassName + "." + seed.Member)}";
            issues.Add(Warning(LineOf(id), "ImpactSeedNotFound", $"작업 {id}: 영향 탐색 시작점을 찾지 못했습니다{where}"));
        }

        // One warning per place, however many seeds reach it: on the first seed's line, naming every seed.
        // Every task counts here, done ones included: a target already handled earlier is not missing.
        var places = result.Targets
            .Where(t => !spec.Tasks.Any(task => Covers(task, t)))
            .GroupBy(t => (Path: Normalize(t.Path), t.Member), PlaceComparer.Instance);
        foreach (var place in places)
        {
            var first = place.First();
            var ids = string.Join(", ", place.Select(t => t.SeedTaskId).Distinct(StringComparer.Ordinal));
            issues.Add(Warning(LineOf(first.SeedTaskId), "ImpactMissing",
                $"작업 {ids}의 영향 대상이 명세에 없습니다: {place.Key.Path}#{place.Key.Member} ({first.Reason})"));
        }

        return issues;
    }

    /// <summary>
    /// True when <paramref name="task"/> names <paramref name="target"/>: same file (OS path comparison, either separator)
    /// and, for a cs task, the same member name. The one place the match rule lives, so sql targets can be added here.
    /// </summary>
    public static bool Covers(SpecTask task, ImpactTarget target)
    {
        if (task.Kind != TaskKind.CSharp || task.Symbol is null) return false;
        return PathComparers.ForOs.Equals(Normalize(task.Path), Normalize(target.Path))
            && string.Equals(SpecSymbol.Split(task.Symbol).Member, target.Member, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="task"/> names <paramref name="target"/>. A SQL object target (design §7.4) is named by a sql
    /// task whose object (<paramref name="sqlObjects"/>, from <see cref="SqlTaskObjects"/>) is the same object — case,
    /// brackets and an omitted <c>dbo</c> ignored. Any other target goes through the cs rule of <see cref="Covers(SpecTask, ImpactTarget)"/>.
    /// </summary>
    public static bool Covers(SpecTask task, ImpactTarget target, IReadOnlyDictionary<string, SqlObjectName> sqlObjects)
    {
        if (target.TargetKind != ImpactTargetKind.SqlObject) return Covers(task, target);
        if (task.Kind != TaskKind.Sql || !sqlObjects.TryGetValue(task.Id, out var obj)) return false;
        return TryParse(target.Member) is { } name && obj.SameAs(name);
    }

    /// <summary>
    /// The not-done sql tasks the SQL search starts from, each with its object: the task's <c>#객체</c>, else the file's only
    /// definition. A task whose object cannot be told (several definitions, unreadable file) is in <c>NotFound</c>.
    /// </summary>
    public static (IReadOnlyList<SqlImpactSeed> Seeds, IReadOnlyList<string> NotFound) SqlSeeds(IFileSystem fs, Workspace ws, ChangeSpec spec, int compatLevel)
    {
        var seeds = new List<SqlImpactSeed>();
        var notFound = new List<string>();
        foreach (var task in spec.Tasks.Where(t => t is { Kind: TaskKind.Sql, Done: false }))
        {
            if (TaskObject(fs, ws, task, compatLevel) is { } name) seeds.Add(new SqlImpactSeed(task.Id, task.Line, name));
            else notFound.Add(task.Id);
        }
        return (seeds, notFound);
    }

    /// <summary>Every sql task's object, done ones included (a target handled earlier is not missing); tasks without one are left out.</summary>
    public static IReadOnlyDictionary<string, SqlObjectName> SqlTaskObjects(IFileSystem fs, Workspace ws, ChangeSpec spec, int compatLevel)
    {
        var objects = new Dictionary<string, SqlObjectName>(StringComparer.Ordinal);
        foreach (var task in spec.Tasks.Where(t => t.Kind == TaskKind.Sql))
            if (TaskObject(fs, ws, task, compatLevel) is { } name) objects[task.Id] = name;
        return objects;
    }

    /// <summary>
    /// Warnings for one SQL impact result (design §7.5): which database, an incomplete string walk, seeds that were not
    /// searched, each missing target (an SP target names the <c>pelly sql pull</c> that would fetch it) and one summary
    /// per table a seed writes. <paramref name="basis"/> is the database the search was asked to use, named when it could
    /// not be read; <paramref name="listPath"/> is the full list's display path, null when it could not be saved.
    /// </summary>
    public static IReadOnlyList<SpecIssue> SqlIssues(ChangeSpec spec, IReadOnlyList<SqlImpactSeed> seeds, IReadOnlyList<string> notFound, SqlImpactResult result,
        IReadOnlyDictionary<string, SqlObjectName> sqlObjects, string pullDirectory, string? listPath, SqlImpactBasis basis)
    {
        var issues = new List<SpecIssue>();
        if (result.Unavailable is not null)
            issues.Add(Warning(0, "ImpactSqlUnavailable",
                $"영향 탐색(SQL): {(basis == SqlImpactBasis.Prod ? "운영" : "개발")} DB에 접속하지 못해 SP 쪽 영향을 찾지 못했습니다: {result.Unavailable}"));
        else if (result.Basis == SqlImpactBasis.Dev)
            issues.Add(Warning(0, "ImpactSqlDevBased", "영향 탐색: 운영 연결이 설정되지 않아 개발 DB 기준으로 찾았습니다"));
        if (result.Incomplete)
            issues.Add(Warning(0, "ImpactIncomplete", "영향 탐색: 일부 폴더·파일을 읽지 못해 결과가 불완전합니다"));

        var lines = seeds.GroupBy(s => s.TaskId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Line, StringComparer.Ordinal);
        int LineOf(string taskId) => lines.GetValueOrDefault(taskId);

        foreach (var id in notFound)
        {
            var task = spec.Tasks.FirstOrDefault(t => t.Id == id);
            issues.Add(Warning(task?.Line ?? 0, "ImpactSeedNotFound",
                $"작업 {id}: 영향 탐색 시작점을 찾지 못했습니다{(task is null ? "" : $": {Normalize(task.Path)}")} (#객체를 적으세요)"));
        }

        foreach (var id in result.NewObjectSeeds)
        {
            var seed = seeds.FirstOrDefault(s => s.TaskId == id);
            var name = seed is null ? "" : SqlImpactFinder.Display(seed.Object);
            issues.Add(Warning(LineOf(id), "ImpactSeedNotFound", $"작업 {id}: {name}가 서버에 없어(새 객체) SP 쪽 영향을 찾지 않았습니다"));
        }

        var pull = Normalize(pullDirectory).TrimEnd('/');
        var missing = result.Targets.Where(t => !spec.Tasks.Any(task => Covers(task, t, sqlObjects)));
        var places = missing.GroupBy(t => t.TargetKind == ImpactTargetKind.SqlObject
            ? (Path: "", Member: t.Member.ToUpperInvariant())
            : (Path: Normalize(t.Path), t.Member), PlaceComparer.Instance);
        foreach (var place in places)
        {
            var first = place.First();
            var ids = string.Join(", ", place.Select(t => t.SeedTaskId).Distinct(StringComparer.Ordinal));
            var message = first.TargetKind == ImpactTargetKind.SqlObject
                ? $"작업 {ids}의 영향 대상이 명세에 없습니다: {first.Member} ({first.Reason}) — pelly sql pull {first.Member} 로 받아 작업 줄에 추가할 수 있습니다 ({(pull.Length == 0 ? "" : pull + "/")}{first.Member}.sql)"
                : $"작업 {ids}의 영향 대상이 명세에 없습니다: {place.Key.Path}#{place.Key.Member} ({first.Reason})";
            issues.Add(Warning(LineOf(first.SeedTaskId), "ImpactMissing", message));
        }

        foreach (var table in result.Tables)
        {
            var columns = table.WrittenColumns.Count == 0 ? "없음" : string.Join(", ", table.WrittenColumns);
            issues.Add(Warning(LineOf(table.SeedTaskId), "ImpactTableShared",
                $"작업 {table.SeedTaskId}: {SqlImpactFinder.Display(table.Table)}를 함께 쓰는 모듈 {table.Modules}개 — 쓰는 컬럼({columns})을 읽는 모듈 {table.Warned}개 경고, " +
                $"외 {table.Omitted}개 생략, 컬럼 확인 불가 {table.Unverifiable}개. 전체 목록: {listPath ?? "(저장 실패)"}"));
        }

        return issues;
    }

    /// <summary>The task's <c>#객체</c>, else its file's only definition; null when neither tells one object.</summary>
    private static SqlObjectName? TaskObject(IFileSystem fs, Workspace ws, SpecTask task, int compatLevel)
    {
        if (task.Symbol is not null) return TryParse(task.Symbol);
        try
        {
            var full = SpecTaskPaths.FullPath(fs.Path, ws.Root, task.Path);
            if (!fs.File.Exists(full)) return null;
            var batches = SqlBatchSplitter.Split(TextFileCodec.Read(fs, full).Text, compatLevel).ObjectBatches;
            return batches.Count == 1 ? batches[0].ObjectName : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static SqlObjectName? TryParse(string text)
    {
        try { return SqlObjectName.Parse(text); }
        catch (FormatException) { return null; }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>Same place: OS path comparison (separators already normalized) and ordinal member name.</summary>
    private sealed class PlaceComparer : IEqualityComparer<(string Path, string Member)>
    {
        public static readonly PlaceComparer Instance = new();

        public bool Equals((string Path, string Member) x, (string Path, string Member) y) =>
            PathComparers.ForOs.Equals(x.Path, y.Path) && string.Equals(x.Member, y.Member, StringComparison.Ordinal);

        public int GetHashCode((string Path, string Member) obj) =>
            HashCode.Combine(PathComparers.ForOs.GetHashCode(obj.Path), StringComparer.Ordinal.GetHashCode(obj.Member));
    }

    private static SpecIssue Warning(int line, string code, string message) => new(line, SpecIssueSeverity.Warning, code, message);
}
