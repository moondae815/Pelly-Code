using System.IO.Abstractions;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Workspaces;
using Workspace = PellyCoder.Core.Workspaces.Workspace;

namespace PellyCoder.Core.Specs;

/// <param name="Impact">The impact search result; null when no search ran or it threw.</param>
/// <param name="SqlImpact">The SQL impact search result; null when none ran or it threw.</param>
/// <param name="SqlImpactSummary">What the SQL impact search did, for <c>apply.json</c>; null when none ran.</param>
public sealed record SpecCheckResult(ChangeSpec Spec, Workspace Workspace, IReadOnlyList<SpecIssue> Issues, ImpactResult? Impact = null,
    SqlImpactResult? SqlImpact = null, SqlImpactSummary? SqlImpactSummary = null)
{
    public bool HasErrors => Issues.Any(i => i.Severity == SpecIssueSeverity.Error);
}

/// <summary>What a SQL impact search needs before a database is chosen: the finder, the <c>sql</c> options and the reader factory.</summary>
public sealed record SqlImpactSetup(ISqlImpactFinder Finder, SqlOptions Options, Func<string, ISqlDefinitionReader> Readers);

/// <summary>One SQL impact search to run (design §7.1, §7.2).</summary>
/// <param name="Db">The opened database; with a <c>Problem</c> the finder runs only the C# string search.</param>
/// <param name="Basis">The database that was asked for (named in the warning when it cannot be read).</param>
/// <param name="PullDirectory">The <c>sql.pullDirectory</c> named in a missing-SP warning.</param>
/// <param name="ListPath">Full path of the full-list JSON, overwritten by each search.</param>
public sealed record SqlImpactRequest(IFileSystem Fs, ISqlImpactFinder Finder, SqlReaderOpen Db, SqlImpactBasis Basis, int CompatLevel,
    string PullDirectory, string ListPath);

/// <summary>Static checks of a change spec against the workspace (no model, no DB): grammar, targets, AC tests, names.</summary>
public static partial class SpecChecker
{

    public static SpecCheckResult Check(IFileSystem fs, string specPath, int sqlCompatLevel)
    {
        var spec = ChangeSpecParser.Parse(TextFileCodec.Read(fs, specPath).Text);
        var ws = WorkspaceLocator.Resolve(fs, specPath);
        var issues = new List<SpecIssue>(spec.Issues);
        var scans = new Scans(fs, ws.Root);

        foreach (var task in spec.Tasks)
            CheckTask(fs, ws, task, sqlCompatLevel, scans, issues);

        CheckAcTests(fs, ws, spec, issues);

        return new SpecCheckResult(spec, ws, issues);
    }

    /// <summary>
    /// <see cref="Check"/>, then — when it found no error and the spec has seeds — the impact search, whose findings are
    /// added as warnings. A search that throws (anything but a cancellation) becomes the warning <c>ImpactFailed</c>:
    /// the warning feature never blocks a spec.
    /// </summary>
    /// <param name="impactAllCsTasks">
    /// <c>spec check --impact</c>: seed every not-done cs task, not just the grouped ones, and — with <paramref name="sql"/> —
    /// run the SQL search from every not-done sql task on the production database, or the development one when no
    /// production connection is configured. Without it no database is touched.
    /// </param>
    public static async Task<SpecCheckResult> CheckAsync(
        IFileSystem fs, string specPath, int sqlCompatLevel, IImpactFinder? impact, bool impactAllCsTasks, CancellationToken ct, SqlImpactSetup? sql = null)
    {
        var check = Check(fs, specPath, sqlCompatLevel);
        var request = impactAllCsTasks && sql is not null && !check.HasErrors
            ? SqlRequest(fs, sql, allowDev: true, SpecCheckListPath(fs, check, specPath))
            : null;
        return await AddImpactAsync(check, impact, impactAllCsTasks, ct, request);
    }

    /// <summary><c>.pelly/impact/&lt;spec id&gt;-sql.json</c>: the full list of a <c>spec check --impact</c>, overwritten by each run.</summary>
    public static string SpecCheckListPath(IFileSystem fs, SpecCheckResult check, string specPath) =>
        fs.Path.Combine(check.Workspace.PellyDir, "impact", (check.Spec.Id ?? fs.Path.GetFileNameWithoutExtension(specPath)) + "-sql.json");

    /// <summary>
    /// The SQL search to run (design §7.1, §7.2): on production when <c>sql.production</c> is configured — even when it
    /// cannot be opened, so the search never silently turns to development; otherwise on development only when
    /// <paramref name="allowDev"/> (<c>spec check --impact</c>), else null (no search). Creating a reader never connects.
    /// </summary>
    public static SqlImpactRequest? SqlRequest(IFileSystem fs, SqlImpactSetup setup, bool allowDev, string listPath)
    {
        var o = setup.Options;
        var prod = SqlReaders.Production(o, setup.Readers);
        if (prod.Reader is not null || prod.Problem is not null)
            return new SqlImpactRequest(fs, setup.Finder, prod, SqlImpactBasis.Prod, o.CompatLevel, o.PullDirectory, listPath);
        if (!allowDev) return null;
        return new SqlImpactRequest(fs, setup.Finder, SqlReaders.Development(o, setup.Readers), SqlImpactBasis.Dev, o.CompatLevel, o.PullDirectory, listPath);
    }

    /// <summary>
    /// The impact half of <see cref="CheckAsync"/>, for a caller that refuses cheaply between the static check and the search.
    /// The C# search (<paramref name="impact"/>) and the SQL search (<paramref name="sql"/>) are independent; either may run.
    /// </summary>
    public static async Task<SpecCheckResult> AddImpactAsync(SpecCheckResult check, IImpactFinder? impact, bool impactAllCsTasks, CancellationToken ct,
        SqlImpactRequest? sql = null)
    {
        if (check.HasErrors) return check;
        check = await AddCsImpactAsync(check, impact, impactAllCsTasks, ct);
        return sql is null ? check : await AddSqlImpactAsync(check, sql, ct);
    }

    private static async Task<SpecCheckResult> AddSqlImpactAsync(SpecCheckResult check, SqlImpactRequest sql, CancellationToken ct)
    {
        var (seeds, notFound) = ImpactCheck.SqlSeeds(sql.Fs, check.Workspace, check.Spec, sql.CompatLevel);
        if (seeds.Count == 0 && notFound.Count == 0) return check;
        var objects = ImpactCheck.SqlTaskObjects(sql.Fs, check.Workspace, check.Spec, sql.CompatLevel);

        if (seeds.Count == 0)
        {
            var none = new SqlImpactResult(null, null, [], [], [], false, new Dictionary<string, IReadOnlyList<SqlImpactModuleRow>>());
            var only = ImpactCheck.SqlIssues(check.Spec, seeds, notFound, none, objects, sql.PullDirectory, null, sql.Basis);
            return check with { Issues = [.. check.Issues, .. only], SqlImpactSummary = new SqlImpactSummary(null, 0, only.Count, null) };
        }

        SqlImpactResult result;
        try
        {
            result = await sql.Finder.FindAsync(check.Workspace, seeds, sql.Db, sql.Basis, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A raw SqlException or anything else: the warning feature never blocks a spec.
            var failed = new SpecIssue(0, SpecIssueSeverity.Warning, "ImpactFailed", $"영향 탐색(SQL)을 할 수 없습니다: {ex.Message}");
            return check with { Issues = [.. check.Issues, failed], SqlImpactSummary = new SqlImpactSummary(null, seeds.Count, 1, null) };
        }

        string? shown = null;
        try
        {
            SqlImpactList.Write(sql.Fs, sql.ListPath, check.Spec.Id, seeds, result);
            shown = sql.Fs.Path.GetRelativePath(check.Workspace.Root, sql.ListPath).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // The warnings still say what was found; the list just says it was not saved.
        }

        // The C# search may already have said the walk was incomplete: say it once.
        var issues = ImpactCheck.SqlIssues(check.Spec, seeds, notFound, result, objects, sql.PullDirectory, shown, sql.Basis)
            .Where(i => !check.Issues.Contains(i))
            .ToList();
        var basis = result.Basis switch { SqlImpactBasis.Prod => "prod", SqlImpactBasis.Dev => "dev", _ => null };
        return check with
        {
            Issues = [.. check.Issues, .. issues],
            SqlImpact = result,
            SqlImpactSummary = new SqlImpactSummary(basis, seeds.Count, issues.Count, shown),
        };
    }

    private static async Task<SpecCheckResult> AddCsImpactAsync(SpecCheckResult check, IImpactFinder? impact, bool impactAllCsTasks, CancellationToken ct)
    {
        if (impact is null) return check;
        var seeds = ImpactCheck.Seeds(check.Spec, impactAllCsTasks);
        if (seeds.Count == 0) return check;

        ImpactResult result;
        try
        {
            result = await impact.FindAsync(check.Workspace, seeds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = new SpecIssue(0, SpecIssueSeverity.Warning, "ImpactFailed", $"영향 탐색을 할 수 없습니다: {ex.Message}");
            return check with { Issues = [.. check.Issues, failed] };
        }

        return check with { Issues = [.. check.Issues, .. ImpactCheck.Issues(check.Spec, seeds, result)], Impact = result };
    }

    /// <summary>
    /// Acceptance-criterion tests: an AC with a not-done linked cs task is judged (test and breaks must resolve exactly,
    /// else errors); other ACs only warn. Also flags an AC without linked cs tasks, and any not-done cs task that edits the test of a judged AC.
    /// </summary>
    private static void CheckAcTests(IFileSystem fs, Workspace ws, ChangeSpec spec, List<SpecIssue> issues)
    {
        var cache = new Dictionary<string, IReadOnlyList<TestMethodDecl>>(StringComparer.Ordinal);
        IReadOnlyList<TestMethodDecl> Find(string name) =>
            cache.TryGetValue(name, out var d) ? d : cache[name] = TestMethodIndex.Find(fs, ws.Root, name);

        foreach (var link in TestFirstLinks.Build(spec))
        {
            var linked = link.LinkedTaskIds.Select(id => spec.Tasks.First(t => t.Id == id)).ToList();
            var judged = linked.Any(t => !t.Done);
            var severity = judged ? SpecIssueSeverity.Error : SpecIssueSeverity.Warning;

            foreach (var name in new[] { link.Test }.Concat(link.Breaks))
            {
                var found = Find(name);
                var distinct = found.Select(f => f.FullName).Distinct(StringComparer.Ordinal).ToList();
                if (found.Count == 0)
                    issues.Add(new SpecIssue(0, severity, "TestNotFound", $"수용 기준 {link.AcId}의 테스트 {name}을(를) 작업 공간에서 찾을 수 없습니다 (테스트 특성이 붙은 메서드만 찾습니다)."));
                else if (distinct.Count > 1)
                    issues.Add(new SpecIssue(0, severity, "TestAmbiguous", $"수용 기준 {link.AcId}의 테스트 {name}이(가) 여러 테스트와 맞습니다: {string.Join(", ", distinct)}"));
            }

            if (linked.Count == 0)
            {
                issues.Add(new SpecIssue(0, SpecIssueSeverity.Warning, "TestUnverified", $"수용 기준 {link.AcId}은(는) 테스트로 검증되지 않습니다 (연결된 cs 작업이 없음)."));
                continue;
            }

            // Any not-done cs task, linked or not, would change the test between its RED and GREEN (design §5.6).
            if (!judged) continue;
            var decls = Find(link.Test);
            foreach (var task in spec.Tasks.Where(t => t.Kind == TaskKind.CSharp && !t.Done && t.Symbol is not null))
            {
                var taskFile = SpecTaskPaths.FullPath(fs.Path, ws.Root, task.Path);
                var member = SpecSymbol.Split(task.Symbol!).Member;
                if (decls.Any(d => SpecTaskPaths.Comparer.Equals(fs.Path.GetFullPath(d.File), taskFile)
                                   && d.FullName[(d.FullName.LastIndexOf('.') + 1)..] == member))
                    issues.Add(new SpecIssue(task.Line, SpecIssueSeverity.Error, "TestIsTarget", $"작업 {task.Id}: 수용 기준 {link.AcId}의 테스트 메서드를 고치는 작업입니다. 테스트는 사람이 씁니다."));
            }
        }
    }

    private static void CheckTask(IFileSystem fs, Workspace ws, SpecTask task, int compat, Scans scans, List<SpecIssue> issues)
    {
        var before = issues.Count;
        void Error(string code, string message) => issues.Add(new SpecIssue(task.Line, SpecIssueSeverity.Error, code, message));

        var full = fs.Path.GetFullPath(fs.Path.Combine(ws.Root, task.Path.Replace('\\', '/')));
        if (!IsInside(fs, ws.Root, full)) { Error("OutsideWorkspace", $"작업 {task.Id}: 경로가 작업 공간 밖입니다: {task.Path}"); return; }
        if (!fs.File.Exists(full)) { Error("FileNotFound", $"작업 {task.Id}: 파일이 없습니다: {task.Path}"); return; }
        var expectedExt = task.Kind == TaskKind.CSharp ? ".cs" : ".sql";
        if (!string.Equals(fs.Path.GetExtension(full), expectedExt, StringComparison.OrdinalIgnoreCase))
        {
            Error("KindMismatch", $"작업 {task.Id}: {(task.Kind == TaskKind.CSharp ? "cs" : "sql")} 작업의 파일이 {expectedExt}가 아닙니다: {task.Path}");
            return;
        }

        FileSnapshot snapshot;
        try { snapshot = TextFileCodec.Read(fs, full); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Error("FileUnreadable", $"작업 {task.Id}: 파일을 읽을 수 없습니다: {task.Path} ({ex.Message})");
            return;
        }

        string? csprojDir = null;
        if (task.Kind == TaskKind.CSharp)
        {
            if (task.Symbol is null) { Error("SymbolRequired", $"작업 {task.Id}: cs 작업은 #심볼로 대상 멤버를 지정해야 합니다."); return; }
            // A done task is never run again, and its target may be gone by design (a group renamed it): --resume must not trip on it.
            if (task.Done) return;
            var csproj = ProjectFileLocator.FindNearestCsproj(fs, full);
            LanguageTarget language;
            try { language = LanguageVersionDetector.Detect(fs, csproj); }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                Error("ProjectFileUnreadable", $"작업 {task.Id}: 프로젝트 파일을 읽을 수 없습니다: {csproj} ({ex.Message})");
                return;
            }

            csprojDir = csproj is null ? ws.Root : fs.Path.GetDirectoryName(csproj)!;
            var (className, member) = SpecSymbol.Split(task.Symbol);
            var found = MemberLocator.FindByName(snapshot.Text, language.Version, className, member);
            if (found.Count == 0) Error("SymbolNotFound", $"작업 {task.Id}: 심볼을 찾을 수 없습니다: {task.Symbol}");
            else if (found.Count > 1) Error("SymbolAmbiguous", $"작업 {task.Id}: 심볼 {task.Symbol}가 {found.Count}곳과 맞습니다: {string.Join(", ", found.Select(f => f.Display))}");
        }
        else
        {
            if (task.Done) return;
            var batches = SqlBatchSplitter.Split(snapshot.Text, compat).ObjectBatches;
            if (task.Symbol is not null)
            {
                SqlObjectName? wanted = null;
                try { wanted = SqlObjectName.Parse(task.Symbol); }
                catch (FormatException ex) { Error("SqlObjectNotFound", $"작업 {task.Id}: SQL 객체 이름이 올바르지 않습니다: {ex.Message}"); }
                if (wanted is not null && !batches.Any(b => b.ObjectName?.SameAs(wanted) == true))
                    Error("SqlObjectNotFound", $"작업 {task.Id}: SQL 객체를 찾을 수 없습니다: {task.Symbol}");
            }
            else if (batches.Count != 1)
                Error("SqlObjectAmbiguous", $"작업 {task.Id}: #객체 없이는 파일에 객체 배치가 정확히 하나여야 합니다(현재 {batches.Count}개).");
        }

        if (issues.Count > before) return; // names in the request are only worth checking against a valid target
        var known = task.Kind == TaskKind.CSharp ? scans.CsIdentifiers(csprojDir!) : scans.SqlIdentifiers();
        foreach (var word in RequestIdentifiers.Extract(task.Request))
        {
            // A dotted or backticked word is known when every identifier-like part of it is.
            var parts = IdentifierPart().Matches(word).Select(m => m.Value).Where(p => !char.IsDigit(p[0]));
            if (parts.Any(p => !known.Contains(p)))
                issues.Add(new SpecIssue(task.Line, SpecIssueSeverity.Warning, "UnknownIdentifier", $"작업 {task.Id}: 요청문의 이름 {word}을(를) 소스에서 찾을 수 없습니다."));
        }
    }

    private static bool IsInside(IFileSystem fs, string root, string full)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.TrimEnd(fs.Path.DirectorySeparatorChar, fs.Path.AltDirectorySeparatorChar) + fs.Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, cmp);
    }

    [GeneratedRegex("[A-Za-z0-9_]+")]
    private static partial Regex IdentifierPart();

    /// <summary>Lazy, cached bounded scans of the workspace sources.</summary>
    private sealed partial class Scans(IFileSystem fs, string root)
    {
        private readonly Dictionary<string, HashSet<string>> cs = new(StringComparer.Ordinal);
        private HashSet<string>? sql;

        /// <summary>Identifier tokens (Roslyn tokenizer) of every .cs file under the project folder.</summary>
        public HashSet<string> CsIdentifiers(string dir)
        {
            if (cs.TryGetValue(dir, out var set)) return set;
            set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var text in Read(dir, "*.cs"))
                foreach (var t in CSharpSyntaxTree.ParseText(text).GetRoot().DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
                    set.Add(t.ValueText);
            return cs[dir] = set;
        }

        /// <summary>Identifier-looking words of every .sql file (regex; T-SQL is case-insensitive).</summary>
        public HashSet<string> SqlIdentifiers()
        {
            if (sql is not null) return sql;
            sql = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in Read(root, "*.sql"))
                foreach (Match m in SqlWord().Matches(text)) sql.Add(m.Value);
            return sql;
        }

        private IEnumerable<string> Read(string dir, string pattern) =>
            BoundedSources.Read(fs, dir, pattern).Select(f => f.Text);

        [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*")]
        private static partial Regex SqlWord();
    }
}
