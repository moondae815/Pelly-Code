using System.IO.Abstractions;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Web;
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

    /// <summary>The SP → ASP calls (A2 design §4.3), covered ones included; null when that search did not run or threw.</summary>
    public IReadOnlyList<AspImpactHit>? AspImpact { get; init; }
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

/// <summary>One SP → ASP impact search to run (A2 design §4.3): files only, no database.</summary>
/// <param name="Asp">The <c>asp</c> options: site root, extensions, encoding and wrapper functions.</param>
/// <param name="ListPath">Full path of the full-list JSON: the SQL search's list when that runs, else one of its own.</param>
public sealed record AspImpactRequest(IFileSystem Fs, AspOptions Asp, int CompatLevel, string ListPath, WebOptions? Web = null);

/// <summary>Static checks of a change spec against the workspace (no model, no DB): grammar, targets, AC tests, names.</summary>
public static partial class SpecChecker
{

    /// <param name="asp">The <c>asp</c> options (extensions, encoding, site root); null means the defaults.</param>
    /// <param name="web">The <c>web</c> options (vendor paths) for js and css tasks; null means the defaults.</param>
    public static SpecCheckResult Check(IFileSystem fs, string specPath, int sqlCompatLevel, AspOptions? asp = null, WebOptions? web = null)
    {
        asp ??= new AspOptions();
        web ??= new WebOptions();
        var spec = ChangeSpecParser.Parse(TextFileCodec.Read(fs, specPath).Text);
        var ws = WorkspaceLocator.Resolve(fs, specPath);
        var issues = new List<SpecIssue>(spec.Issues);
        var scans = new Scans(fs, ws.Root);

        foreach (var task in spec.Tasks)
            CheckTask(fs, ws, task, sqlCompatLevel, asp, web, scans, issues);

        CheckLinesAfterEdit(fs, ws, spec, issues);
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
    /// production connection is configured, and the SP → ASP search (A2 design §4.3). Without it no database is touched.
    /// </param>
    public static async Task<SpecCheckResult> CheckAsync(
        IFileSystem fs, string specPath, int sqlCompatLevel, IImpactFinder? impact, bool impactAllCsTasks, CancellationToken ct, SqlImpactSetup? sql = null,
        AspOptions? asp = null, WebOptions? web = null)
    {
        var check = Check(fs, specPath, sqlCompatLevel, asp, web);
        var request = impactAllCsTasks && sql is not null && !check.HasErrors
            ? SqlRequest(fs, sql, allowDev: true, SpecCheckListPath(fs, check, specPath))
            : null;
        var aspRequest = impactAllCsTasks && !check.HasErrors
            ? new AspImpactRequest(fs, asp ?? new AspOptions(), sqlCompatLevel, SpecCheckListPath(fs, check, specPath), web)
            : null;
        return await AddImpactAsync(check, impact, impactAllCsTasks, ct, request, aspRequest);
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
    /// The C# search (<paramref name="impact"/>), the SQL search (<paramref name="sql"/>) and the SP → ASP search
    /// (<paramref name="asp"/>) are independent; any may run. The ASP calls go into the SQL search's full list when that
    /// search saves one, else — when there are any — into a list of their own at <see cref="AspImpactRequest.ListPath"/>.
    /// </summary>
    public static async Task<SpecCheckResult> AddImpactAsync(SpecCheckResult check, IImpactFinder? impact, bool impactAllCsTasks, CancellationToken ct,
        SqlImpactRequest? sql = null, AspImpactRequest? asp = null)
    {
        if (check.HasErrors) return check;
        check = await AddCsImpactAsync(check, impact, impactAllCsTasks, ct);

        (IReadOnlyList<SqlImpactSeed> Seeds, IReadOnlyList<AspImpactHit> Hits)? found = null;
        SpecIssue? aspFailed = null;
        if (asp is not null)
        {
            try
            {
                found = FindAsp(check, asp);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The warning feature never blocks a spec.
                aspFailed = new SpecIssue(0, SpecIssueSeverity.Warning, "ImpactFailed", $"영향 탐색(ASP)을 할 수 없습니다: {ex.Message}");
            }
        }

        if (sql is not null) check = await AddSqlImpactAsync(check, sql, ct, found?.Hits);
        if (aspFailed is not null) return check with { Issues = [.. check.Issues, aspFailed] };
        return found is { } f ? AddAspIssues(check, asp!, f.Seeds, f.Hits) : check;
    }

    /// <summary>The ASP calls of the stored procedures the not-done sql tasks change; null when there is no such task.</summary>
    private static (IReadOnlyList<SqlImpactSeed> Seeds, IReadOnlyList<AspImpactHit> Hits)? FindAsp(SpecCheckResult check, AspImpactRequest request)
    {
        var (seeds, _) = ImpactCheck.SqlSeeds(request.Fs, check.Workspace, check.Spec, request.CompatLevel);
        if (seeds.Count == 0) return null;

        var fs = request.Fs;
        var o = request.Asp;
        var root = check.Workspace.Root;
        var siteRoot = AspSiteRoot.Resolve(fs, o.SiteRoot, root);
        if (!fs.Directory.Exists(siteRoot)) return (seeds, []);

        // The W2 site index and its cache: the same pages are not parsed twice.
        var codec = new WebCodecRouter(new WebTextCodec(o.Encoding), new AspTextCodec(o.Encoding), o.Extensions);
        var cachePath = fs.Path.Combine(root, ".pelly", "cache", "web-site-index.json");
        var index = WebSiteIndex.Load(fs, siteRoot, new WebFileFilter(siteRoot, (request.Web ?? new WebOptions()).VendorPaths), codec, cachePath,
            aspExtensions: o.Extensions, spCallFunctions: o.SpCallFunctions);

        // The index's paths are site-relative: so are the asp tasks' for the comparison.
        var tasks = check.Spec.Tasks
            .Select(t => t.Kind != TaskKind.Asp ? t
                : t with { Path = fs.Path.GetRelativePath(siteRoot, SpecTaskPaths.FullPath(fs.Path, root, t.Path)).Replace('\\', '/') })
            .ToList();
        var pageCodec = new AspTextCodec(o.Encoding);
        AspPage? PageOf(SpecTask task)
        {
            try
            {
                var full = fs.Path.Combine(siteRoot, task.Path);
                return AspPage.Parse(pageCodec.Decode(full, fs.File.ReadAllBytes(full)).Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        return (seeds, AspImpactFinder.Find(seeds, tasks, index, PageOf));
    }

    /// <summary>
    /// The SP → ASP warnings. When the SQL search did not save a list with the calls, their own list is saved first and
    /// recorded as the summary's list (apply.json): when there is a call, or — with an empty <c>asp</c> — when an older list
    /// is at that path, so stale rows never survive. With neither, nothing is written (an apply without callers stays as before).
    /// </summary>
    private static SpecCheckResult AddAspIssues(SpecCheckResult check, AspImpactRequest request, IReadOnlyList<SqlImpactSeed> seeds,
        IReadOnlyList<AspImpactHit> hits)
    {
        var shown = check.SqlImpactSummary?.ListPath;
        var ownList = false;
        if (shown is null && (hits.Count > 0 || request.Fs.File.Exists(request.ListPath)))
        {
            try
            {
                SqlImpactList.WriteAsp(request.Fs, request.ListPath, check.Spec.Id, seeds, hits);
                shown = request.Fs.Path.GetRelativePath(check.Workspace.Root, request.ListPath).Replace('\\', '/');
                ownList = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // The warnings still say what was found; the list just says it was not saved.
            }
        }
        var issues = ImpactCheck.AspIssues(seeds, hits, shown);
        var summary = ownList && check.SqlImpactSummary is null ? new SqlImpactSummary(null, seeds.Count, issues.Count, shown) : check.SqlImpactSummary;
        return check with { Issues = [.. check.Issues, .. issues], AspImpact = hits, SqlImpactSummary = summary };
    }

    private static async Task<SpecCheckResult> AddSqlImpactAsync(SpecCheckResult check, SqlImpactRequest sql, CancellationToken ct,
        IReadOnlyList<AspImpactHit>? asp)
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
            result = await sql.Finder.FindAsync(check.Workspace, seeds, objects.Values.ToList(), sql.Db, sql.Basis, ct);
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
            SqlImpactList.Write(sql.Fs, sql.ListPath, check.Spec.Id, seeds, result, asp);
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

    private static void CheckTask(IFileSystem fs, Workspace ws, SpecTask task, int compat, AspOptions asp, WebOptions web, Scans scans,
        List<SpecIssue> issues)
    {
        var before = issues.Count;
        void Error(string code, string message) => issues.Add(new SpecIssue(task.Line, SpecIssueSeverity.Error, code, message));

        var full = fs.Path.GetFullPath(fs.Path.Combine(ws.Root, task.Path.Replace('\\', '/')));
        if (!IsInside(fs, ws.Root, full)) { Error("OutsideWorkspace", $"작업 {task.Id}: 경로가 작업 공간 밖입니다: {task.Path}"); return; }
        if (!fs.File.Exists(full)) { Error("FileNotFound", $"작업 {task.Id}: 파일이 없습니다: {task.Path}"); return; }
        if (task.Kind == TaskKind.Asp) { CheckAspTask(fs, task, full, asp, issues); return; }
        if (task.Kind is TaskKind.Js or TaskKind.Css) { CheckWebTask(fs, ws, task, full, asp, web, issues); return; }
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

    /// <summary>
    /// An asp task (design §3.3): the extension is one of <c>asp.extensions</c>, the file decodes, and the target — a procedure or
    /// a line range — exists. No request-name check: there is no VBScript name index and it would mostly misfire.
    /// </summary>
    private static void CheckAspTask(IFileSystem fs, SpecTask task, string full, AspOptions asp, List<SpecIssue> issues)
    {
        void Error(string code, string message) => issues.Add(new SpecIssue(task.Line, SpecIssueSeverity.Error, code, message));

        var ext = fs.Path.GetExtension(full);
        if (!asp.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            Error("KindMismatch", $"작업 {task.Id}: asp 작업의 파일이 {string.Join(", ", asp.Extensions)} 중 하나가 아닙니다: {task.Path}");
            return;
        }
        if (task.Done) return;

        string text;
        try { text = new AspTextCodec(asp.Encoding).Decode(full, fs.File.ReadAllBytes(full)).Text; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Error("AspEncoding", $"작업 {task.Id}: 파일을 읽을 수 없습니다: {task.Path} ({ex.Message})");
            return;
        }

        if (task.Symbol is null && task.Lines is null)
        {
            Error("AspTargetRequired", $"작업 {task.Id}: asp 작업은 #프로시저 또는 #L시작-끝으로 대상을 지정해야 합니다.");
            return;
        }

        var page = AspPage.Parse(text);
        if (task.Lines is { } lines)
        {
            if (lines.EndLine > page.LineCount)
                Error("LinesOutOfFile", $"작업 {task.Id}: 줄 범위 L{lines.StartLine}-{lines.EndLine}가 파일(1-{page.LineCount}줄) 밖입니다");
            return;
        }

        var found = MatchingProcedures(page, task.Symbol!);
        if (found.Count == 0) Error("SymbolNotFound", $"작업 {task.Id}: 심볼을 찾을 수 없습니다: {task.Symbol}");
        else if (found.Count > 1)
            Error("SymbolAmbiguous", $"작업 {task.Id}: 심볼 {task.Symbol}가 {found.Count}곳과 맞습니다: {string.Join(", ", found.Select(f => $"{f.Keyword} {(f.ClassName is null ? "" : f.ClassName + ".")}{f.Name} (줄 {f.DeclLine})"))}");
    }

    /// <summary>
    /// A js or css task (W3 design §3.3): extension, readable text, not a minified or vendor file, a target, and that target —
    /// a function, a selector or a line range that, on a page, lies inside one script or style block.
    /// </summary>
    private static void CheckWebTask(IFileSystem fs, Workspace ws, SpecTask task, string full, AspOptions asp, WebOptions web, List<SpecIssue> issues)
    {
        void Error(string code, string message) => issues.Add(new SpecIssue(task.Line, SpecIssueSeverity.Error, code, message));

        var keyword = ChangeSpecParser.KindKeyword(task.Kind);
        var own = task.Kind == TaskKind.Js ? ".js" : ".css";
        var allowed = new[] { own }.Concat(WebPages.PageExtensions(asp.Extensions)).ToList();
        var ext = fs.Path.GetExtension(full);
        if (!allowed.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            Error("KindMismatch", $"작업 {task.Id}: {keyword} 작업의 파일이 {string.Join(", ", allowed)} 중 하나가 아닙니다: {task.Path}");
            return;
        }
        if (task.Done) return;

        var isPage = WebPages.IsPage(full, asp.Extensions);
        string text;
        try
        {
            var codec = new WebCodecRouter(new WebTextCodec(asp.Encoding), new AspTextCodec(asp.Encoding), asp.Extensions);
            text = codec.Decode(full, fs.File.ReadAllBytes(full)).Text;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Error("WebEncoding", $"작업 {task.Id}: 파일을 읽을 수 없습니다: {task.Path} ({ex.Message})");
            return;
        }

        if (!isPage)
        {
            var siteRoot = AspSiteRoot.Resolve(fs, asp.SiteRoot, ws.Root);
            var (verdict, reason) = new WebFileFilter(siteRoot, web.VendorPaths).Classify(full, text);
            if (verdict != WebFileVerdict.Ok)
            {
                Error("WebVendorFile", $"작업 {task.Id}: 고칠 수 없는 파일입니다: {task.Path} — {reason}");
                return;
            }
        }

        if (task.Symbol is null && task.Lines is null)
        {
            var what = task.Kind == TaskKind.Js ? "#함수" : "#선택자";
            Error("WebTargetRequired", $"작업 {task.Id}: {keyword} 작업에는 대상({what} 또는 #L시작-끝)이 필요합니다");
            return;
        }

        if (task.Lines is { } lines)
        {
            var lineCount = WebLocateCommon.LineCount(text);
            if (lines.EndLine > lineCount)
                Error("LinesOutOfFile", $"작업 {task.Id}: 줄 범위 L{lines.StartLine}-{lines.EndLine}가 파일(1-{lineCount}줄) 밖입니다");
            else if (isPage && !WebTargetLookup.LinesInOneBlock(AspPage.Parse(text), lines, task.Kind == TaskKind.Js ? EditKind.Js : EditKind.Css))
                Error("LinesOutsideBlock",
                    $"작업 {task.Id}: 줄 범위가 {(task.Kind == TaskKind.Js ? "<script>" : "<style>")} 블록 하나 안에 있지 않습니다. asp 작업으로 쓰십시오");
            return;
        }

        var found = task.Kind == TaskKind.Js
            ? WebTargetLookup.Function(text, isPage, task.Symbol!)
            : WebTargetLookup.Rule(text, isPage, task.Symbol!);
        if (found == WebTargetFind.NotFound) Error("SymbolNotFound", $"작업 {task.Id}: 심볼을 찾을 수 없습니다: {task.Symbol}");
        else if (found == WebTargetFind.Ambiguous) Error("SymbolAmbiguous", $"작업 {task.Id}: 심볼 {task.Symbol}가 여러 곳과 맞습니다");
    }

    /// <summary>The same rule as <c>pelly asp --proc</c>: <c>Class.Name</c> is that class member; a bare name is top-level, else class members.</summary>
    internal static List<AspProcedure> MatchingProcedures(AspPage page, string wanted)
    {
        static bool Same(string? a, string b) => a is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);
        var dot = wanted.IndexOf('.');
        if (dot >= 0)
            return page.Procedures.Where(p => Same(p.ClassName, wanted[..dot]) && Same(p.Name, wanted[(dot + 1)..])).ToList();
        var named = page.Procedures.Where(p => Same(p.Name, wanted)).ToList();
        var topLevel = named.Where(p => p.ClassName is null).ToList();
        return topLevel.Count > 0 ? topLevel : named;
    }

    /// <summary>
    /// A line-range task after another not-done task of any kind on the same file: the earlier edit may shift its lines (error,
    /// W3 design §3.2). An asp task keeps the A2 code <c>AspLinesAfterEdit</c>; a js or css task gets <c>LinesAfterEdit</c>.
    /// </summary>
    private static void CheckLinesAfterEdit(IFileSystem fs, Workspace ws, ChangeSpec spec, List<SpecIssue> issues)
    {
        var firstByFile = new Dictionary<string, SpecTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in spec.Tasks.Where(t => !t.Done))
        {
            var key = SpecTaskPaths.FullPath(fs.Path, ws.Root, task.Path).Replace('\\', '/');
            if (!firstByFile.TryGetValue(key, out var first)) { firstByFile[key] = task; continue; }
            if (task.Lines is not null)
                issues.Add(new SpecIssue(task.Line, SpecIssueSeverity.Error, task.Kind == TaskKind.Asp ? "AspLinesAfterEdit" : "LinesAfterEdit",
                    $"{task.Id}의 줄 범위는 같은 파일을 먼저 고치는 {first.Id} 뒤에 어긋날 수 있습니다. 프로시저 대상(#이름)으로 쓰거나 작업을 하나로 합치십시오"));
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
