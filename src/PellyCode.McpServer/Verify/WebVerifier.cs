using System.IO.Abstractions;
using System.Text;
using Acornima;
using PellyCode.Core.Abstractions;
using PellyCode.Core.Asp;
using PellyCode.Core.Builds;
using PellyCode.Core.Config;
using PellyCode.Core.Files;
using PellyCode.Core.Impact;
using PellyCode.Core.Models;
using PellyCode.Core.Web;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Verify;

/// <summary>
/// The ASP·JS·CSS part of <c>pelly_verify</c> (design §7). Each changed page, <c>.js</c> and <c>.css</c> file is checked with Core's
/// pure checks on its HEAD text (「수정 전」) and its current text; every other file — includes, <c>&lt;script src&gt;</c> files, the
/// site index — is read from the live tree on both sides, except that a changed include is put back to its HEAD text on the
/// 「수정 전」 side. Errors: new VBScript compile errors (cscript, Windows only) and new undefined ASP names
/// (<see cref="AspNameCheck"/>). Everything else is a warning: page elements, JS names and APIs, form fields, CSS properties, and
/// the calls a changed JS signature left behind. The pages that include a changed file get the name and element checks
/// (at most <see cref="MaxIncluderPages"/> per include); pages that use a changed <c>.css</c> are not checked.
/// </summary>
public sealed class WebVerifier(IFileSystem fs, ServerContext ctx, IProcessRunner processes, Func<bool> isWindows)
{
    public const int MaxIncluderPages = 50;
    public const string SkipHeading = "[건너뜀] ASP·JS·CSS 검사";
    public const string NoSettings = "설정에 asp·web 절이 없습니다";
    public const string SyntaxSkipHeading = "[건너뜀] ASP 구문 검사";
    public const string NotWindows = "Windows가 아님";

    private static readonly TimeSpan CscriptTimeout = TimeSpan.FromSeconds(30);

    private AspOptions Asp => ctx.Options.Asp;
    private WebOptions Web => ctx.Options.Web;
    private IFileSystem Fs => fs;
    private ServerContext Ctx => ctx;
    private IProcessRunner Processes => processes;
    private bool OnWindows => isWindows();

    private enum FileKind { Asp, Html, Js, Css }

    /// <param name="Rel">Workspace-relative, '/'.</param>
    /// <param name="SiteRel">Site-relative, '/' (the key of the site index).</param>
    /// <param name="Before">The HEAD text (LF); empty for an added file.</param>
    /// <param name="After">The current text (LF).</param>
    private sealed record Item(string Rel, string Full, string SiteRel, FileKind Kind, string Before, string After)
    {
        public bool IsPage => Kind is FileKind.Asp or FileKind.Html;
    }

    public bool IsWeb(string path) =>
        WebPages.IsPage(path, Asp.Extensions) || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);

    /// <param name="head">The HEAD bytes of a repository-relative path; null when HEAD has no such file.</param>
    public async Task CheckAsync(IReadOnlyList<GitChange> changes, Func<string, CancellationToken, Task<byte[]?>> head, VerifyReport report,
        IProgress<string>? progress, CancellationToken ct)
    {
        var changed = changes.Where(c => c.Kind != GitChangeKind.Deleted && IsWeb(c.Path)).ToList();
        if (changed.Count == 0) return;
        if (!Asp.Configured && !Web.Configured)
        {
            report.Skip(SkipHeading, NoSettings);
            return;
        }
        progress?.Report("바뀐 ASP·JS·CSS 파일을 검사하는 중입니다");

        var run = new Run(this, report);
        var items = new List<Item>();
        foreach (var change in changed)
            if (await run.LoadAsync(change, head, ct) is { } item) items.Add(item);
        if (items.Count == 0) return;
        await run.CheckAsync(items, ct);
    }

    /// <summary>One verify call's shared state: the site, its index, the HEAD texts of the changed files and the sections found.</summary>
    private sealed class Run
    {
        private readonly WebVerifier _owner;
        private readonly VerifyReport _report;
        private readonly IFileSystem _fs;
        private readonly WebSiteAccess _access;
        private readonly string _siteRoot;
        private readonly WebCodecRouter _codec;
        private readonly AspTextCodec _aspCodec;
        private readonly Sections _sections = new();
        private readonly Dictionary<string, string> _headByFull;
        private readonly Dictionary<string, string> _headBySite = new(StringComparer.OrdinalIgnoreCase);
        private WebSiteIndexResult? _index;
        private IReadOnlyList<JqueryFound>? _jquery;
        private bool? _adoKnown;
        private bool _syntaxSkipped;

        public Run(WebVerifier owner, VerifyReport report)
        {
            _owner = owner;
            _report = report;
            _fs = owner.Fs;
            _access = new WebSiteAccess(_fs, owner.Asp, owner.Web, owner.Ctx.Root);
            _siteRoot = _access.SiteRoot;
            _codec = new WebCodecRouter(new WebTextCodec(owner.Asp.Encoding), new AspTextCodec(owner.Asp.Encoding), owner.Asp.Extensions);
            _aspCodec = new AspTextCodec(owner.Asp.Encoding);
            _headByFull = new Dictionary<string, string>(PathComparers.ForOs);
        }

        private AspOptions Asp => _owner.Asp;
        private WebOptions Web => _owner.Web;

        // ---- loading ----

        public async Task<Item?> LoadAsync(GitChange change, Func<string, CancellationToken, Task<byte[]?>> head, CancellationToken ct)
        {
            var full = _fs.Path.GetFullPath(_fs.Path.Combine(_owner.Ctx.Root, change.Path.Replace('/', _fs.Path.DirectorySeparatorChar)));
            var kind = KindOf(full);
            var section = Section(kind, change.Path);
            try
            {
                var bytes = _fs.File.ReadAllBytes(full);
                var current = _codec.Decode(full, bytes);
                if (kind is FileKind.Js or FileKind.Css)
                {
                    var (verdict, reason) = new WebFileFilter(_siteRoot, Web.VendorPaths).Classify(full, current.Text);
                    if (verdict != WebFileVerdict.Ok)
                    {
                        _report.Skip(section, reason ?? (verdict == WebFileVerdict.Minified ? "압축된 파일" : "외부 라이브러리"));
                        return null;
                    }
                }
                var headBytes = change.Kind == GitChangeKind.Added ? null : await head(change.OldPath ?? change.Path, ct);
                var before = headBytes is null ? "" : HeadText.Decode(headBytes, current, bytes, b => _codec.Decode(full, b));
                var siteRel = _fs.Path.GetRelativePath(_siteRoot, full).Replace('\\', '/');
                var item = new Item(change.Path, full, siteRel, kind, before, HeadText.Lf(current.Text));
                _headByFull[full] = before;
                _headBySite[siteRel] = before;
                return item;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _report.Skip(section, $"파일을 읽지 못했습니다: {e.Message}");
                return null;
            }
        }

        private FileKind KindOf(string path)
        {
            if (Asp.Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return FileKind.Asp;
            if (WebPages.IsPage(path, Asp.Extensions)) return FileKind.Html;
            return path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? FileKind.Css : FileKind.Js;
        }

        private static string Section(FileKind kind, string rel) => kind switch
        {
            FileKind.Asp => "[ASP] " + rel,
            FileKind.Css => "[CSS] " + rel,
            _ => "[JS] " + rel,
        };

        // ---- the checks ----

        public async Task CheckAsync(IReadOnlyList<Item> items, CancellationToken ct)
        {
            if (items.Any(i => i.Kind == FileKind.Asp) && !_owner.OnWindows)
            {
                _report.Skip(SyntaxSkipHeading, NotWindows);
                _syntaxSkipped = true;
            }

            foreach (var item in items)
            {
                var section = Section(item.Kind, item.Rel);
                _sections.Open(section);
                if (item.Before == item.After) continue;
                try
                {
                    switch (item.Kind)
                    {
                        case FileKind.Css:
                            CheckCss(section, item);
                            break;
                        case FileKind.Js:
                            CheckJs(section, item.Rel, item.Before, item.After, JsNameCheck.SiteNames(Index, null), FileJquery);
                            break;
                        default:
                            await CheckPageAsync(section, item, ct);
                            break;
                    }
                    if (item.Kind != FileKind.Css) CheckSignatures(section, item);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    _sections.NoPass(section);
                    _report.Skip(section, $"검사 중 파일을 읽지 못했습니다: {e.Message}");
                }
            }
            try
            {
                CheckFormFields(items);
                CheckIncluders(items);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _report.Note($"폼 필드·include하는 페이지 검사 중 파일을 읽지 못해 일부를 건너뜀: {e.Message}");
            }
            _sections.Flush(_report);
        }

        private async Task CheckPageAsync(string section, Item item, CancellationToken ct)
        {
            var beforePage = AspPage.Parse(item.Before);
            var afterPage = AspPage.Parse(item.After);
            if (item.Kind == FileKind.Asp)
            {
                if (!_syntaxSkipped) await CheckSyntaxAsync(section, item, ct);
                CheckNames(section, item.Rel, item.Full, beforePage, afterPage);
            }
            CheckElements(section, item.Full, beforePage, item.Before, afterPage, item.After);
            CheckPageScripts(section, item, beforePage, afterPage);
        }

        /// <summary>VBScript compile errors of the current page that the HEAD page did not have (cscript stops at the first one).</summary>
        private async Task CheckSyntaxAsync(string section, Item item, CancellationToken ct)
        {
            var after = await CompileAsync(item.Rel, item.After, ct);
            if (after.Skipped is { } reason)
            {
                _report.Skip(SyntaxSkipHeading, reason);
                _syntaxSkipped = true;
                return;
            }
            if (after.Errors.Count == 0) return;
            var before = await CompileAsync(item.Rel, item.Before, ct);
            if (before.Skipped is { } beforeReason)
            {
                _report.Skip(SyntaxSkipHeading, beforeReason);
                _syntaxSkipped = true;
                return;
            }
            foreach (var e in BaselineDiff.New(before.Errors, after.Errors))
                _sections.Error(section, $"{item.Rel}:{e.Line} VBScript 컴파일 오류: {e.Message}");
        }

        private sealed record Compiled(IReadOnlyList<Diagnostic> Errors, string? Skipped);

        /// <summary>The page through <see cref="VbsCompileScript"/> and cscript, as pelly's AspCompile step does; the script is deleted after.</summary>
        private async Task<Compiled> CompileAsync(string rel, string textLf, CancellationToken ct)
        {
            var script = VbsCompileScript.Build(AspPage.Parse(textLf), textLf);
            var dir = _fs.Path.Combine(_owner.Ctx.Workspace.PellyDir, "mcp-verify");
            _fs.Directory.CreateDirectory(dir);
            var path = _fs.Path.Combine(dir, $"asp-compile-{Guid.NewGuid():N}.vbs");
            var body = Encoding.Unicode.GetBytes(script.Text.Replace("\n", "\r\n", StringComparison.Ordinal));
            _fs.File.WriteAllBytes(path, [.. Encoding.Unicode.GetPreamble(), .. body]);
            ProcessResult result;
            try
            {
                result = await _owner.Processes.RunAsync(
                    new ProcessSpec("cscript", ["//Nologo", "//T:10", "//E:vbscript", path], dir, CscriptTimeout), ct);
            }
            catch (ProcessStartException ex)
            {
                return new Compiled([], FirstLine(ex.Message) ?? "cscript를 실행할 수 없습니다");
            }
            finally
            {
                try { _fs.File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }

            if (result.TimedOut) return new Compiled([], $"cscript 시간 초과({CscriptTimeout.TotalSeconds:0}초)");
            var output = TextDecoding.Decode(result.StdOutBytes) + "\n" + TextDecoding.Decode(result.StdErrBytes ?? []);
            if (result.ExitCode is not (0 or 1)) return new Compiled([], FirstLine(output) ?? $"cscript 종료 코드 {result.ExitCode}");
            if (result.ExitCode == 1 && !output.Contains("Microsoft VBScript", StringComparison.Ordinal))
                return new Compiled([], FirstLine(output) ?? "cscript 종료 코드 1");
            return new Compiled(CscriptOutput.Parse(output, path, script).Select(e => e with { File = rel }).ToList(), null);
        }

        private static string? FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

        /// <summary>
        /// Names that no declaration, procedure, include or built-in explains, new against the HEAD page with HEAD includes
        /// (errors; warnings when the check cannot be sure). <paramref name="before"/> and <paramref name="after"/> may be the same
        /// page (an includer of a changed include): then only the include side differs.
        /// </summary>
        private void CheckNames(string section, string rel, string full, AspPage before, AspPage after)
        {
            var afterIncludes = AspIncludeResolver.Resolve(_fs, full, after, _siteRoot, _aspCodec);
            var beforeIncludes = SameIncludes(before, after) ? afterIncludes : AspIncludeResolver.Resolve(_fs, full, before, _siteRoot, _aspCodec);
            beforeIncludes = AtHead(beforeIncludes);
            if (ReferenceEquals(before, after) && ReferenceEquals(beforeIncludes, afterIncludes)) return;

            var result = NewNames(before, beforeIncludes, after, afterIncludes);
            foreach (var u in result.Unknown)
            {
                var line = $"{rel}:{u.Line} 정의되지 않은 이름: {u.Name}";
                if (result.Downgraded) _sections.Warning(section, $"{line} — {result.DowngradeReason}");
                else _sections.Error(section, line);
            }
        }

        private static bool SameIncludes(AspPage a, AspPage b) =>
            a.Includes.Select(i => (i.Path, i.Virtual)).SequenceEqual(b.Includes.Select(i => (i.Path, i.Virtual)));

        /// <summary>The include set with every changed include parsed from its HEAD text; the same instance when none is in it.</summary>
        private AspIncludeSet AtHead(AspIncludeSet set)
        {
            var swapped = false;
            var files = set.Files.Select(f =>
            {
                if (!_headByFull.TryGetValue(_fs.Path.GetFullPath(f.Path), out var text)) return f;
                swapped = true;
                return f with { Page = AspPage.Parse(text) };
            }).ToList();
            return swapped ? set with { Files = files } : set;
        }

        /// <summary>
        /// <see cref="AspNameCheck.NewUnknown"/> when both sides see the same includes; otherwise the same count comparison over
        /// each side's own include set (each side's unknown uses taken against an empty original).
        /// </summary>
        private AspNameResult NewNames(AspPage before, AspIncludeSet beforeIncludes, AspPage after, AspIncludeSet afterIncludes)
        {
            var builtins = AspBuiltins.Default;
            if (ReferenceEquals(beforeIncludes, afterIncludes))
                return AspNameCheck.NewUnknown(before, after, afterIncludes, builtins, AdoKnown);

            var empty = AspPage.Parse("");
            var baseline = AspNameCheck.NewUnknown(empty, before, beforeIncludes, builtins, AdoKnown);
            var ado = AdoKnown || baseline.Unknown.Any(u => builtins.IsAdoConstant(u.Name));
            if (ado && !AdoKnown) baseline = AspNameCheck.NewUnknown(empty, before, beforeIncludes, builtins, true);
            var now = AspNameCheck.NewUnknown(empty, after, afterIncludes, builtins, ado);
            var counts = baseline.Unknown.GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var fresh = new List<AspUnknownName>();
            foreach (var g in now.Unknown.GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
            {
                var extra = g.Count() - counts.GetValueOrDefault(g.Key);
                if (extra > 0) fresh.AddRange(g.OrderBy(u => u.Line).TakeLast(extra));
            }
            return new AspNameResult(fresh.OrderBy(u => u.Line).ToList(), now.Downgraded, now.DowngradeReason);
        }

        /// <summary><c>asp.adoConstants</c>, or a typelib METADATA in <c>&lt;siteRoot&gt;/global.asa</c>.</summary>
        private bool AdoKnown => _adoKnown ??= Asp.AdoConstants || GlobalAsaHasTypeLib();

        private bool GlobalAsaHasTypeLib()
        {
            var path = _fs.Path.Combine(_siteRoot, "global.asa");
            try
            {
                return _fs.File.Exists(path)
                    && AspNameCheck.HasTypeLibMetadata(AspPage.Parse(_aspCodec.Decode(path, _fs.File.ReadAllBytes(path)).Text));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return false;
            }
        }

        /// <summary>JS lookups of ids and names that no longer find their element (warnings), with related files at HEAD on the before side.</summary>
        private void CheckElements(string section, string full, AspPage before, string beforeText, AspPage after, string afterText)
        {
            if (!HasScript(before) && !HasScript(after)) return;
            var afterRelated = PageScripts.RelatedTexts(_fs, _siteRoot, full, after, _codec, Index);
            var beforeRelated = AtHead(SameIncludes(before, after) && SameScripts(before, after)
                ? afterRelated
                : PageScripts.RelatedTexts(_fs, _siteRoot, full, before, _codec, Index));
            foreach (var w in NewBroken(before, beforeText, beforeRelated, after, afterText, afterRelated))
                _sections.Warning(section, w);
        }

        private static bool HasScript(AspPage page) => page.ClientBlocks.Any(b => b.Kind == AspClientBlockKind.Script);

        private static bool SameScripts(AspPage a, AspPage b) =>
            a.ClientBlocks.Select(c => c.Src).SequenceEqual(b.ClientBlocks.Select(c => c.Src));

        private IReadOnlyList<RelatedText> AtHead(IReadOnlyList<RelatedText> related) =>
            related.Any(r => _headBySite.ContainsKey(r.File))
                ? related.Select(r => _headBySite.TryGetValue(r.File, out var text) ? r with { TextLf = text } : r).ToList()
                : related;

        /// <summary>
        /// <see cref="PageElements.NewBroken"/> when both sides see the same related files; otherwise the same count comparison with
        /// each side's own related files (the warning without its hints).
        /// </summary>
        private static IReadOnlyList<string> NewBroken(AspPage before, string beforeText, IReadOnlyList<RelatedText> beforeRelated,
            AspPage after, string afterText, IReadOnlyList<RelatedText> afterRelated)
        {
            if (ReferenceEquals(beforeRelated, afterRelated))
                return PageElements.NewBroken(before, beforeText, after, afterText, afterRelated);

            static List<ElementDef> Defs(string text, IReadOnlyList<RelatedText> related) =>
                PageElements.Definitions(text, null).Concat(related.SelectMany(r => PageElements.Definitions(r.TextLf, r.File))).ToList();
            var beforeDefs = Defs(beforeText, beforeRelated);
            var afterDefs = Defs(afterText, afterRelated);
            var counts = PageElements.References(before, beforeText).Where(r => Broken(r, beforeDefs))
                .GroupBy(r => (r.Kind, r.Value)).ToDictionary(g => g.Key, g => g.Count());
            return PageElements.References(after, afterText).Where(r => Broken(r, afterDefs))
                .GroupBy(r => (r.Kind, r.Value))
                .Where(g => g.Count() > counts.GetValueOrDefault(g.Key))
                .Select(g => g.MinBy(r => r.Line)!)
                .OrderBy(r => r.Line).ThenBy(r => r.Value, StringComparer.Ordinal)
                .Select(r => r.Kind == ElementKind.Id ? $"#{r.Value}(줄 {r.Line}) — id 없음" : $"[name={r.Value}](줄 {r.Line}) — name 없음")
                .ToList();
        }

        /// <summary>The same rule as <see cref="PageElements"/>: no definition of that kind and value (a <c>*</c> value is a prefix).</summary>
        private static bool Broken(ElementRef r, IReadOnlyList<ElementDef> defs) =>
            !defs.Any(d => d.Kind == r.Kind && (d.Value == r.Value
                || (d.Value.EndsWith('*') && r.Value.StartsWith(d.Value[..^1], StringComparison.Ordinal))));

        /// <summary>Each inline JavaScript block whose text changed, paired by position when both sides have the same blocks.</summary>
        private void CheckPageScripts(string section, Item item, AspPage before, AspPage after)
        {
            var a = after.ClientBlocks;
            var b = before.ClientBlocks;
            if (a.Count != b.Count || a.Where((block, i) => block.Kind != b[i].Kind).Any()) return;
            for (var i = 0; i < a.Count; i++)
            {
                if (!PageBlockView.IsJavaScriptBlock(a[i]) || !PageBlockView.IsJavaScriptBlock(b[i])) continue;
                var beforeView = PageBlockView.Build(before, item.Before, i);
                var afterView = PageBlockView.Build(after, item.After, i);
                if (beforeView.Text == afterView.Text) continue;
                if (beforeView.ServerCode == PageServerCode.Statements || afterView.ServerCode == PageServerCode.Statements) continue;
                var scripts = PageScripts.Collect(_fs, _siteRoot, item.Full, after, i, _codec, Index, JqueryFound, Web.JqueryVersion, item.After);
                CheckJs(section, item.Rel, beforeView.Text, afterView.Text, JsNameCheck.SiteNames(Index, scripts.VisibleNames),
                    (scripts.Jquery, scripts.Jquery is not null || JqueryFound.Count > 0));
            }
        }

        /// <summary>
        /// New free names (as pelly js's Names row) and new ES/jQuery APIs (Api row) of one piece of JS, at the HEAD text's level
        /// raised to <c>web.jsTarget</c>. A HEAD text that does not parse has no tree to compare; a current text that no longer
        /// parses is one warning.
        /// </summary>
        private void CheckJs(string section, string rel, string before, string after, IReadOnlySet<string> siteNames,
            (Version? Version, bool Loaded) jquery)
        {
            var detected = JsLevels.Detect(before);
            if (detected.Level is not { } level) return;
            var target = level;
            var setting = Web.JsTarget ?? "auto";
            if (!setting.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) && JsLevels.FromSetting(setting) is { } wanted
                && JsLevels.Compare(wanted, level) > 0)
                target = wanted;

            var original = JsDocument.Parse(before, level, detected.Module);
            var patched = JsDocument.Parse(after, target, detected.Module);
            if (original.Error is not null) return;
            if (patched.Error is { } error)
            {
                _sections.Warning(section, $"{rel}:{error.Line} JS 구문 오류({JsLevels.Name(target)} 기준): {error.Description}");
                return;
            }

            var known = JsNameCheck.KnownSet(WebBuiltins.Default, jquery.Loaded, siteNames, Web.Globals);
            var unknown = JsNameCheck.NewUnknown(original, patched, known)
                .Where(u => !PageBlockView.IsPlaceholder(u.Name))
                .GroupBy(u => u.Name, StringComparer.Ordinal)
                .Select(g => (Name: g.Key, Line: g.Min(u => u.Line)))
                .OrderBy(u => u.Line).ThenBy(u => u.Name, StringComparer.Ordinal);
            foreach (var u in unknown) _sections.Warning(section, $"{rel}:{u.Line} 정의되지 않은 JS 이름: {u.Name}");
            foreach (var w in JsApiCheck.NewWarnings(original, patched, target, jquery.Version, WebBuiltins.Default))
                _sections.Warning(section, w);
        }

        private void CheckCss(string section, Item item)
        {
            foreach (var w in CssPropertyCheck.NewUnknown(CssSheet.Parse(item.Before), CssSheet.Parse(item.After), WebBuiltins.Default))
                _sections.Warning(section, w);
        }

        /// <summary>
        /// The JS functions whose name or parameter count changed, and their calls in the site that still use the old name or pass
        /// another number of arguments (as pelly apply's end check, from the site index as the files are now).
        /// </summary>
        private void CheckSignatures(string section, Item item)
        {
            if (item.Before.Length == 0) return;
            IReadOnlyList<JsSignatureChange> changes;
            try
            {
                changes = JsSignatureChanges.InLines(item.Before, item.After, item.IsPage, new TextSpanInfo(1, item.Before.Split('\n').Length));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return; // a warning check: a text it cannot read is no finding
            }
            foreach (var change in changes)
            {
                if (change.Renamed)
                {
                    foreach (var (rel, call) in Callers(item.SiteRel, change.Before.Name))
                        _sections.Warning(section, $"{Workspace(rel)}:{call.Line} {change.Before.Name} 호출이 남아 있음 — 이름이 바뀜: {change.Display}");
                }
                else if (change.CountChanged)
                {
                    foreach (var (rel, call) in Callers(item.SiteRel, change.After.Name))
                        if (call.Arguments is { } n && n != change.After.Parameters.Count)
                            _sections.Warning(section, $"{Workspace(rel)}:{call.Line} {change.After.Name} 호출의 인수가 {n}개 — 서명이 바뀜: {change.Display}");
                }
            }
        }

        /// <summary>Calls of <paramref name="name"/>, without those of files that declare that name themselves.</summary>
        private IEnumerable<(string RelativePath, JsCallSite Call)> Callers(string seedPath, string name) =>
            Index.JsCalls.Where(c => c.Call.Name == name && !c.Call.Reference
                    && (string.Equals(c.RelativePath, seedPath, StringComparison.OrdinalIgnoreCase)
                        || !JsImpactFinder.DeclaresOwn(Index, c.RelativePath, name, seedPath)))
                .OrderBy(c => c.RelativePath, StringComparer.Ordinal).ThenBy(c => c.Call.Line);

        /// <summary>Form field pairs the changes broke (W3 design §5.3), every changed page and <c>.js</c> at HEAD on the before side.</summary>
        private void CheckFormFields(IReadOnlyList<Item> items)
        {
            var scripts = items.Where(i => i.Kind != FileKind.Css && i.Before != i.After).ToList();
            if (scripts.Count == 0) return;
            var before = new Dictionary<string, FileFormFacts>(StringComparer.OrdinalIgnoreCase);
            var any = false;
            foreach (var i in scripts)
            {
                before[i.SiteRel] = Facts(i, i.Before);
                any |= !before[i.SiteRel].IsEmpty || !Facts(i, i.After).IsEmpty;
            }
            if (!any) return;

            foreach (var m in FormSite.NewMismatches(FormSite.Build(Index, before), FormSite.Build(Index), before.Keys.ToList()))
            {
                var owner = new[] { m.SenderFile, m.ReadFile, m.Target }
                    .Select(f => f is null ? null : scripts.FirstOrDefault(i => string.Equals(i.SiteRel, f, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(i => i is not null) ?? scripts[0];
                _sections.Warning(Section(owner.Kind, owner.Rel), m.Message);
            }
        }

        private static FileFormFacts Facts(Item item, string text) =>
            item.Kind == FileKind.Js ? FormFacts.OfScript(text) : FormFacts.OfPage(AspPage.Parse(text), text);

        /// <summary>
        /// The pages that include a changed file get the name and element checks with only the include side at HEAD; at most
        /// <see cref="MaxIncluderPages"/> per include, the rest is a note.
        /// </summary>
        private void CheckIncluders(IReadOnlyList<Item> items)
        {
            var changed = items.Select(i => i.SiteRel).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var include in items.Where(i => i.IsPage && i.Before != i.After))
            {
                var pages = Index.IncludersOf(include.SiteRel).Where(p => !changed.Contains(p)).ToList();
                if (pages.Count > MaxIncluderPages)
                    _report.Note($"{include.Rel}: include하는 페이지가 {pages.Count}개라 {MaxIncluderPages}개만 검사함 — 외 {pages.Count - MaxIncluderPages}개 페이지는 검사하지 않음");
                foreach (var page in pages.Take(MaxIncluderPages))
                    if (done.Add(page)) CheckIncluder(page);
            }
        }

        private void CheckIncluder(string siteRel)
        {
            if (_access.Read(siteRel) is not { } text) return;
            var full = _fs.Path.GetFullPath(_fs.Path.Combine(_siteRoot, siteRel));
            var rel = Workspace(siteRel);
            var kind = KindOf(full);
            var section = Section(kind, rel);
            var page = AspPage.Parse(text);
            if (kind == FileKind.Asp) CheckNames(section, rel, full, page, page);
            if (!HasScript(page)) return;
            var related = PageScripts.RelatedTexts(_fs, _siteRoot, full, page, _codec, Index);
            var atHead = AtHead(related);
            if (ReferenceEquals(atHead, related)) return;
            foreach (var w in NewBroken(page, text, atHead, page, text, related)) _sections.Warning(section, w);
        }

        // ---- the site ----

        private WebSiteIndexResult Index => _index ??= LoadIndex();

        private WebSiteIndexResult LoadIndex()
        {
            try
            {
                var index = _access.Index();
                if (index.Truncated) _report.Note("사이트 색인이 파일 수 상한에서 잘려 빠진 검사가 있을 수 있습니다");
                return index;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _report.Note($"사이트 색인을 읽지 못해 사이트 전체를 보는 검사가 빠졌습니다: {e.Message}");
                return new WebSiteIndexResult([], 0, false);
            }
        }

        private IReadOnlyList<JqueryFound> JqueryFound => _jquery ??= JqueryVersions.Find(_fs, _siteRoot);

        /// <summary>A <c>.js</c> file's jQuery: <c>web.jqueryVersion</c>, else the lowest the site has.</summary>
        private (Version? Version, bool Loaded) FileJquery
        {
            get
            {
                var version = JqueryVersions.Baseline(JqueryFound, Web.JqueryVersion);
                return (version, version is not null || JqueryFound.Count > 0);
            }
        }

        /// <summary>A site-relative path as a workspace-relative one (the site path itself when the site lies outside the root).</summary>
        private string Workspace(string siteRel)
        {
            var full = _fs.Path.GetFullPath(_fs.Path.Combine(_siteRoot, siteRel));
            var rel = _fs.Path.GetRelativePath(_owner.Ctx.Root, full).Replace('\\', '/');
            return rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || _fs.Path.IsPathRooted(rel) ? siteRel : rel;
        }
    }

    /// <summary>The web sections in the order first opened; a section without findings becomes 「통과」.</summary>
    private sealed class Sections
    {
        private readonly List<string> _order = [];
        private readonly Dictionary<string, List<(bool Error, string Line)>> _lines = new(StringComparer.Ordinal);
        private readonly HashSet<string> _opened = new(StringComparer.Ordinal);

        /// <summary>A section of a changed file: it shows 「통과」 when nothing is found.</summary>
        public void Open(string section)
        {
            if (_opened.Add(section) && !_lines.ContainsKey(section)) Add(section);
        }

        /// <summary>A section whose checks did not finish: it shows its findings, never 「통과」.</summary>
        public void NoPass(string section) => _opened.Remove(section);

        public void Error(string section, string line) => Get(section).Add((true, line));

        public void Warning(string section, string line) => Get(section).Add((false, line));

        private List<(bool, string)> Get(string section) => _lines.TryGetValue(section, out var list) ? list : Add(section);

        private List<(bool, string)> Add(string section)
        {
            _order.Add(section);
            return _lines[section] = [];
        }

        public void Flush(VerifyReport report)
        {
            foreach (var section in _order)
            {
                var lines = _lines[section];
                if (lines.Count == 0)
                {
                    if (_opened.Contains(section)) report.Pass(section);
                    continue;
                }
                foreach (var (error, line) in lines)
                    if (error) report.Error(section, line);
                    else report.Warning(section, line);
            }
        }
    }
}
