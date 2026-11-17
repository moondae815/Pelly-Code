using System.Globalization;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCode.Core.CSharp;
using PellyCode.Core.Files;
using PellyCode.Core.Impact;
using PellyCode.Core.Sql;
using PellyCode.Core.Web;

namespace PellyCode.McpServer.Impact;

/// <summary>
/// <c>pelly_impact</c> (design §6) by file kind: a C# member at a line or by name, searched with
/// <see cref="SemanticImpactFinder"/> (name-based fallback inside it); the stored procedures of a <c>.sql</c> file, searched in
/// the site index's ASP calls and in C# string literals; a JS function of a <c>.js</c> file or a page's script block, searched
/// in the site index. Returns the tool's Korean text.
/// </summary>
/// <param name="maxSiteFiles">The site index's limit on pages and on <c>.js</c> files (each); past it the text notes the cut.</param>
public sealed partial class ImpactService(IFileSystem fs, ServerContext ctx, ISemanticLoader loader, int maxSiteFiles = 2000)
{
    [GeneratedRegex(@"^(?<path>.*\.[^.:\\/]+):(?<line>[0-9]{1,9})$")]
    private static partial Regex LineSuffix();

    /// <summary>
    /// An absolute path as given; a relative one against the repository root, else (when only that exists) against the --root
    /// folder the user gave (a subfolder of the repository).
    /// </summary>
    private string FullPath(string path)
    {
        if (fs.Path.IsPathRooted(path)) return fs.Path.GetFullPath(path);
        var full = fs.Path.GetFullPath(fs.Path.Combine(ctx.Root, path));
        if (fs.File.Exists(full) || ctx.GivenRoot == ctx.Root) return full;
        var given = fs.Path.GetFullPath(fs.Path.Combine(ctx.GivenRoot, path));
        return fs.File.Exists(given) ? given : full;
    }

    private const string SeedId = "impact";

    public async Task<string> FindAsync(string path, int? line, string? name, CancellationToken ct)
    {
        // 「File.cs:42」 (the way paths are often quoted) is the file and its line.
        if (line is null && LineSuffix().Match(path) is { Success: true } m)
        {
            path = m.Groups["path"].Value;
            line = int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture);
        }
        var full = FullPath(path);
        var rel = Relative(full);
        if (rel is null) return $"작업 공간 밖의 파일입니다: {path}";

        var extension = fs.Path.GetExtension(full).ToLowerInvariant();
        var isJs = extension == ".js" || WebPages.IsPage(full, ctx.Options.Asp.Extensions);
        if (extension is not (".cs" or ".sql") && !isJs)
            return $"지원하지 않는 파일 형식: {(extension.Length == 0 ? "(확장자 없음)" : extension)}";
        if (!fs.File.Exists(full)) return $"파일이 없습니다: {rel}";
        if (extension == ".sql") return FindSql(full, rel, line, name);
        if (line is null && string.IsNullOrWhiteSpace(name)) return "line이나 name을 주세요";
        if (isJs) return FindJs(full, rel, extension == ".js", line, name);

        string text;
        try { text = TextFileCodec.Read(fs, full).Text; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"파일을 읽지 못했습니다: {rel}";
        }

        var version = LanguageVersionDetector.Detect(fs, ProjectFileLocator.FindNearestCsproj(fs, full)).Version;
        MemberCandidate? candidate;
        if (line is { } l)
        {
            candidate = MemberLocator.FindAtLine(text, version, l);
            if (candidate is null) return $"{rel}:{l} 줄에 메서드나 속성이 없습니다";
        }
        else
        {
            var given = name!.Trim();
            var dot = given.LastIndexOf('.');
            var className = dot < 0 ? null : given[..dot][(given[..dot].LastIndexOf('.') + 1)..];
            candidate = MemberLocator.FindByName(text, version, className, given[(dot + 1)..]).FirstOrDefault();
            if (candidate is null) return $"멤버를 찾지 못했습니다: {given} ({rel})";
        }

        var member = MemberName(candidate);
        var innermostType = candidate.ContainingType[(candidate.ContainingType.LastIndexOf('.') + 1)..];
        var declarationLine = DeclarationLine(text, version, candidate, member);
        var heading = $"대상: {candidate.ContainingType}.{member} ({rel}:{declarationLine})";
        if (member == innermostType || member.StartsWith("operator", StringComparison.Ordinal))
            return heading + "\n" + ImpactFormatter.Unsupported;

        var seed = new ImpactSeed(SeedId, declarationLine, rel, innermostType, member);
        var result = await new SemanticImpactFinder(fs, loader).FindAsync(ctx.Workspace, [seed], ct);
        var types = new Dictionary<string, SyntaxNode?>(StringComparer.Ordinal);
        return ImpactFormatter.Format(heading, result, t => TypeAt(types, t));
    }

    /// <summary>
    /// The object batch around <paramref name="line"/>, else the objects named <paramref name="name"/>, else every object of
    /// the file; their callers in ASP pages (site index) and in C# string literals.
    /// </summary>
    private string FindSql(string full, string rel, int? line, string? name)
    {
        if (ReadText(full) is not { } text) return $"파일을 읽지 못했습니다: {rel}";
        var batches = SqlBatchSplitter.Split(text, ctx.Options.Sql.CompatLevel).ObjectBatches.Where(b => b.ObjectName is not null).ToList();
        List<SqlBatch> picked;
        if (line is { } l)
        {
            picked = batches.Where(b => b.StartLine <= l && l <= b.EndLine).Take(1).ToList();
            if (picked.Count == 0) return $"{rel}:{l} 줄에 SQL 객체(프로시저·함수·뷰)가 없습니다";
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            SqlObjectName wanted;
            try { wanted = SqlObjectName.Parse(name.Trim()); }
            catch (FormatException) { return $"SQL 객체 이름이 올바르지 않습니다: {name.Trim()}"; }
            picked = batches.Where(b => b.ObjectName!.SameAs(wanted)).ToList();
            if (picked.Count == 0) return $"SQL 객체를 찾지 못했습니다: {name.Trim()} ({rel})";
        }
        else
        {
            picked = batches.ToList();
            if (picked.Count == 0) return $"{rel}에 SQL 객체(프로시저·함수·뷰)가 없습니다";
        }

        var objects = new List<SqlObjectName>();
        foreach (var b in picked)
            if (!objects.Any(o => o.SameAs(b.ObjectName!))) objects.Add(b.ObjectName!);
        var heading = objects.Count == 1
            ? $"대상: {objects[0].Display} ({rel}:{CreateLine(text, picked[0])})"
            : $"대상: {string.Join(", ", objects.Select(o => o.Display))} ({rel})";
        string Reason(string reason, int seed) => objects.Count == 1 ? reason : $"{reason} ({objects[seed].Display})";

        var notes = new List<string>();
        var places = new List<ImpactPlace>();
        if (SiteIndex(notes) is var (access, index))
        {
            var seeds = objects.Select((o, i) => new SqlImpactSeed(i.ToString(CultureInfo.InvariantCulture), 0, o)).ToList();
            foreach (var hit in AspImpactFinder.Find(seeds, [], index, _ => null))
                places.Add(new ImpactPlace(SitePath(access, hit.Call.RelativePath), hit.Call.Line, hit.Call.Procedure ?? "페이지 본문",
                    Reason("SP 호출", int.Parse(hit.TaskIds, CultureInfo.InvariantCulture))));
        }

        var nameSeeds = objects.Select((o, i) => new SqlNameSeed(i.ToString(CultureInfo.InvariantCulture), o)).ToList();
        var (targets, incomplete) = SqlNameStringFinder.Find(fs, ctx.Workspace, nameSeeds);
        if (incomplete) notes.Add(ImpactFormatter.Incomplete);
        var types = new Dictionary<string, SyntaxNode?>(StringComparer.Ordinal);
        foreach (var t in targets)
        {
            var who = t.Reason == "멤버 밖" || TypeAt(types, t) is not { } type ? t.Member : $"{type}.{t.Member}";
            places.Add(new ImpactPlace(t.Path, t.Line, who, Reason(t.Reason, int.Parse(t.SeedTaskId, CultureInfo.InvariantCulture))));
        }
        return ImpactFormatter.FormatPlaces(heading, notes, places);
    }

    /// <summary>The first CREATE or ALTER line of the batch (its comments come before it), else the batch's first line.</summary>
    private static int CreateLine(string text, SqlBatch batch)
    {
        var lines = text.Split('\n');
        for (var i = batch.StartLine; i <= batch.EndLine && i <= lines.Length; i++)
            if (CreateOrAlter.IsMatch(lines[i - 1])) return i;
        return batch.StartLine;
    }

    private static readonly Regex CreateOrAlter = new(@"^\s*(CREATE|ALTER)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The JS function around <paramref name="line"/> (innermost) or named <paramref name="name"/>, and its callers in the site index.</summary>
    private string FindJs(string full, string rel, bool isJsFile, int? line, string? name)
    {
        if (!WebConfigured) return ImpactFormatter.NoWebSettings;
        var access = new WebSiteAccess(fs, ctx.Options.Asp, ctx.Options.Web, ctx.Root);
        var siteRel = fs.Path.GetRelativePath(access.SiteRoot, full).Replace('\\', '/');
        if (siteRel == ".." || siteRel.StartsWith("../", StringComparison.Ordinal) || fs.Path.IsPathRooted(siteRel))
            return $"사이트 폴더(asp.siteRoot) 밖의 파일입니다: {rel}";
        if (access.Read(siteRel) is not { } text) return $"파일을 읽지 못했습니다: {rel}";

        var functions = WebTargetLookup.FunctionsIn(text, !isJsFile);
        JsFunction? function;
        if (line is { } l)
        {
            function = Innermost(functions, l);
            if (function is null) return $"{rel}:{l} 줄에 JS 함수가 없습니다";
        }
        else
        {
            var given = name!.Trim();
            function = functions.FirstOrDefault(f => f.QualifiedName == given) ?? functions.FirstOrDefault(f => f.Name == given);
            if (function is null) return $"JS 함수를 찾지 못했습니다: {given} ({rel})";
        }

        var heading = $"대상: {function.QualifiedName} ({rel}:{function.DeclLine})";
        var notes = new List<string>();
        var places = new List<ImpactPlace>();
        if (SiteIndex(notes) is var (_, index))
        {
            var read = access.CachedReader();
            var inFile = new Dictionary<string, IReadOnlyList<JsFunction>>(StringComparer.OrdinalIgnoreCase);
            var seed = new JsImpactSeed(SeedId, siteRel, function.QualifiedName, function.DeclLine);
            foreach (var hit in JsImpactFinder.Find([seed], [], index, read))
            {
                var call = hit.Call;
                string who;
                if (call.Source == JsCallSource.Attribute)
                    who = call.AttributeName is { } attr && !attr.EndsWith(':') ? $"{attr} 속성" : "javascript: 링크";
                else
                {
                    if (!inFile.TryGetValue(hit.RelativePath, out var around))
                        inFile[hit.RelativePath] = around = read(hit.RelativePath) is { } t
                            ? WebTargetLookup.FunctionsIn(t, !hit.RelativePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                            : [];
                    who = Innermost(around, call.Line)?.QualifiedName ?? "스크립트 본문";
                }
                places.Add(new ImpactPlace(SitePath(access, hit.RelativePath), call.Line, who, call.Reference ? "참조" : "호출"));
            }
        }
        return ImpactFormatter.FormatPlaces(heading, notes, places);
    }

    /// <summary>The smallest function whose lines hold <paramref name="line"/>; null when none does.</summary>
    private static JsFunction? Innermost(IReadOnlyList<JsFunction> functions, int line) =>
        functions.Where(f => f.StartLine <= line && line <= f.EndLine).OrderBy(f => f.EndLine - f.StartLine).FirstOrDefault();

    private bool WebConfigured => ctx.Options.Asp.Configured || ctx.Options.Web.Configured;

    /// <summary>
    /// The site and its index when the settings have an <c>asp</c> or <c>web</c> section and the site folder exists;
    /// otherwise null, with the reason added to <paramref name="notes"/>. A cut index is noted too.
    /// </summary>
    private (WebSiteAccess Access, WebSiteIndexResult Index)? SiteIndex(List<string> notes)
    {
        if (!WebConfigured)
        {
            notes.Add(ImpactFormatter.NoWebSettings);
            return null;
        }
        var access = new WebSiteAccess(fs, ctx.Options.Asp, ctx.Options.Web, ctx.Root);
        if (!access.Exists)
        {
            notes.Add($"사이트 폴더가 없어 ASP·JS 부분은 건너뜀: {Relative(access.SiteRoot) ?? access.SiteRoot}");
            return null;
        }
        var index = access.Index(maxSiteFiles);
        if (index.Truncated) notes.Add("사이트 색인이 파일 수 상한에서 잘려 빠진 곳이 있을 수 있습니다");
        if (index.TooLarge > 0) notes.Add($"너무 커서 색인에서 빠진 파일이 {index.TooLarge}개 있습니다");
        return (access, index);
    }

    /// <summary>A site-relative path as a workspace-relative one (the site path itself when the site lies outside the root).</summary>
    private string SitePath(WebSiteAccess access, string siteRelative) =>
        Relative(fs.Path.GetFullPath(fs.Path.Combine(access.SiteRoot, siteRelative))) ?? siteRelative;

    /// <summary>The file's text with LF line ends, decoded as the CLI does (CP949 and BOMs included); null when unreadable.</summary>
    private string? ReadText(string full)
    {
        try { return TextFileCodec.Read(fs, full).Text; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>The member's own name: the display without its containing type and parameter list.</summary>
    private static string MemberName(MemberCandidate candidate)
    {
        var display = candidate.Display[(candidate.ContainingType.Length + 1)..];
        var paren = display.IndexOf('(');
        return paren >= 0 ? display[..paren] : display;
    }

    /// <summary>The 1-based line of the member's name (its doc comment and attributes come before it).</summary>
    private static int DeclarationLine(string text, LanguageVersion version, MemberCandidate candidate, string member)
    {
        var root = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(version)).GetRoot();
        foreach (var node in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var identifier = node switch
            {
                MethodDeclarationSyntax m => m.Identifier,
                PropertyDeclarationSyntax p => p.Identifier,
                ConstructorDeclarationSyntax c => c.Identifier,
                _ => default,
            };
            if (identifier.Text != member) continue;
            var at = identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (candidate.Span.StartLine <= at && at <= candidate.Span.EndLine) return at;
        }
        return candidate.Span.StartLine;
    }

    /// <summary>The innermost type around a found place, read from its file (parsed once per call); null when unknown.</summary>
    private string? TypeAt(Dictionary<string, SyntaxNode?> roots, ImpactTarget target)
    {
        if (target.Line is not { } line || target.Path.Length == 0) return null;
        if (!roots.TryGetValue(target.Path, out var root))
        {
            var full = fs.Path.Combine(ctx.Root, target.Path.Replace('/', fs.Path.DirectorySeparatorChar));
            try { root = CSharpSyntaxTree.ParseText(TextFileCodec.Read(fs, full).Text).GetRoot(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { root = null; }
            roots[target.Path] = root;
        }
        if (root is null) return null;

        return root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(t =>
            {
                var span = t.GetLocation().GetLineSpan();
                return span.StartLinePosition.Line + 1 <= line && line <= span.EndLinePosition.Line + 1;
            })
            .LastOrDefault()?.Identifier.Text;
    }

    /// <summary>Workspace-relative path with '/', or null when the file lies outside the root.</summary>
    private string? Relative(string full)
    {
        var rel = fs.Path.GetRelativePath(ctx.Root, full).Replace('\\', '/');
        return rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || fs.Path.IsPathRooted(rel) ? null : rel;
    }
}
