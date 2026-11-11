namespace PellyCoder.Core.Web;

/// <summary>
/// Everything one file sends to one page, merged (W3 design §5.2: several forms of one page often feed one save page).
/// <paramref name="Fields"/> is the union of the closed senders' fields, null when any sender is open.
/// <paramref name="Line"/> and <paramref name="Kind"/> are the first sender's.
/// </summary>
public sealed record FormSenderGroup(
    string File, string Target, int Line, FormSenderKind Kind, IReadOnlyList<string>? Fields, bool AnyPost, bool AnyGet);

public enum FormMismatchKind { NotSent, NobodySends }

/// <summary>A key a page reads that its senders do not send.</summary>
public sealed record FormMismatch(
    FormMismatchKind Kind, string Target, string Key, string ReadFile, int ReadLine, string? SenderFile, int SenderLine,
    FormSenderKind SenderKind, string? Similar, int Senders)
{
    /// <summary>The same mismatch in another world or run: kind, target, key and sender, ignoring case and lines.</summary>
    public (FormMismatchKind, string, string, string?) Identity =>
        (Kind, Target.ToLowerInvariant(), Key.ToLowerInvariant(), SenderFile?.ToLowerInvariant());

    public string Message
    {
        get
        {
            var where = string.Equals(ReadFile, Target, StringComparison.OrdinalIgnoreCase) ? $"줄 {ReadLine}" : $"{ReadFile} 줄 {ReadLine}";
            var head = $"{Target}가 읽는 {Key}({where})를 ";
            if (Kind == FormMismatchKind.NobodySends) return head + $"보내는 폼·ajax가 없음(보내는 곳 {Senders}곳)";
            var sender = SenderKind == FormSenderKind.Ajax ? "ajax" : "폼";
            var text = head + $"{SenderFile}의 {sender}(줄 {SenderLine})이 보내지 않음";
            return Similar is null ? text : text + $", 보내는 비슷한 이름: {Similar}";
        }
    }
}

/// <summary>
/// The site's form pairs: which page reads which keys and who sends to it (W3 design §5.1). Addresses are resolved here, not
/// in the per-file cache, because they depend on includes and <c>&lt;script src&gt;</c> of other files.
/// </summary>
public sealed class FormSite
{
    private const int IncludeDepth = 5;

    private readonly WebSiteIndexResult _index;
    private readonly Dictionary<string, FileFormFacts> _facts;
    private readonly Dictionary<string, string> _known; // any case → the index's spelling
    private readonly Dictionary<string, List<FormSenderGroup>> _sendersTo = new(StringComparer.OrdinalIgnoreCase);

    private FormSite(WebSiteIndexResult index, Dictionary<string, FileFormFacts> facts, Dictionary<string, string> known)
    {
        _index = index;
        _facts = facts;
        _known = known;
    }

    /// <summary>
    /// The site as the index sees it, with <paramref name="overrides"/> in place of the index's facts for those files
    /// (<see cref="FileFormFacts.Empty"/> means the file has none).
    /// </summary>
    public static FormSite Build(WebSiteIndexResult index, IReadOnlyDictionary<string, FileFormFacts>? overrides = null)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Know(string rel) => known.TryAdd(rel, rel);
        foreach (var rel in index.FormFacts.Keys) Know(rel);
        foreach (var (page, incs) in index.Includes) { Know(page); foreach (var i in incs) Know(i); }
        foreach (var (page, js) in index.ScriptSources) { Know(page); foreach (var j in js) Know(j); }
        foreach (var (rel, _) in index.JsCalls) Know(rel);
        foreach (var e in index.Entries) Know(e.RelativePath);

        var facts = new Dictionary<string, FileFormFacts>(index.FormFacts, StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, f) in overrides ?? new Dictionary<string, FileFormFacts>())
        {
            var key = Normalize(rel);
            Know(key);
            if (f.IsEmpty) facts.Remove(key);
            else facts[key] = f;
        }

        var site = new FormSite(index, facts, known);
        site.PairSenders();
        return site;
    }

    private static string Normalize(string rel) => rel.Replace('\\', '/').TrimStart('/');

    private string Canonical(string rel) => _known.TryGetValue(Normalize(rel), out var k) ? k : Normalize(rel);

    /// <summary>The page and the files it includes (directly or through other includes).</summary>
    private List<string> WithIncludes(string page)
    {
        var start = Canonical(page);
        var result = new List<string> { start };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var frontier = new List<string> { start };
        for (var depth = 0; depth < IncludeDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var f in frontier)
                if (_index.Includes.TryGetValue(f, out var incs))
                    foreach (var i in incs)
                        if (seen.Add(i)) { result.Add(i); next.Add(i); }
            frontier = next;
        }
        return result;
    }

    private FileFormFacts FactsOf(string rel) => _facts.TryGetValue(rel, out var f) ? f : FileFormFacts.Empty;

    /// <summary>The literal-key reads of the page and its includes, page first.</summary>
    public IReadOnlyList<(string Key, FormReadSource Source, string File, int Line)> ReadsOf(string page) =>
        WithIncludes(page).SelectMany(file => FactsOf(file).Reads.Select(r => (r.Key, r.Source, file, r.Line))).ToList();

    /// <summary>True when the page or one of its includes reads the form without a literal key.</summary>
    public bool AllRead(string page) => WithIncludes(page).Any(f => FactsOf(f).AllRead);

    public IReadOnlyList<FormSenderGroup> SendersTo(string page) =>
        _sendersTo.TryGetValue(Canonical(page), out var list) ? list : [];

    // ---- Pairing ------------------------------------------------------------------------------------------------

    private static bool IsInclude(string rel) => rel.EndsWith(".inc", StringComparison.OrdinalIgnoreCase);

    private static bool IsScript(string rel) => rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pages a browser shows <paramref name="rel"/>'s senders in: the page, an include's includers, a script's loaders.</summary>
    private List<string> ShownIn(string rel)
    {
        if (IsScript(rel))
        {
            var pages = new List<string>();
            foreach (var (page, js) in _index.ScriptSources)
                if (js.Contains(rel, StringComparer.OrdinalIgnoreCase))
                    pages.AddRange(IsInclude(page) ? Includers(page) : [page]);
            return pages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return IsInclude(rel) ? Includers(rel) : [rel];
    }

    private List<string> Includers(string inc)
    {
        var pages = _index.IncludersOf(inc).Where(p => !IsInclude(p)).ToList();
        return pages.Count > 0 ? pages : [inc];
    }

    /// <summary>The action assignments a page's JS can make: its own files (page and includes) and the scripts they load.</summary>
    private List<FormAction> ActionsFor(string page)
    {
        var files = WithIncludes(page);
        var scripts = files.SelectMany(f => _index.ScriptSources.TryGetValue(f, out var js) ? js : []);
        return files.Concat(scripts).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(f => FactsOf(f).Actions).ToList();
    }

    private void PairSenders()
    {
        var groups = new Dictionary<(string File, string Target), List<FormSender>>();
        var order = new List<(string File, string Target)>();
        foreach (var (file, facts) in _facts.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (facts.Senders.Count == 0) continue;
            var pages = ShownIn(file);
            foreach (var sender in facts.Senders)
            {
                if (sender.UrlUnknown) continue;
                var targets = new Dictionary<string, FormSender>(StringComparer.OrdinalIgnoreCase);
                foreach (var page in pages)
                    foreach (var (t, s) in Targets(sender, page))
                        targets.TryAdd(t, s);
                foreach (var (t, s) in targets)
                {
                    var key = (file, t);
                    if (!groups.TryGetValue(key, out var list)) { groups[key] = list = []; order.Add(key); }
                    list.Add(s);
                }
            }
        }
        foreach (var key in order)
        {
            var senders = groups[key].OrderBy(s => s.Line).ToList();
            IReadOnlyList<string>? fields = senders.Any(s => s.Fields is null)
                ? null
                : senders.SelectMany(s => s.Fields!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var group = new FormSenderGroup(key.File, key.Target, senders[0].Line, senders[0].Kind, fields,
                senders.Any(s => s.Post), senders.Any(s => !s.Post));
            if (!_sendersTo.TryGetValue(key.Target, out var list)) _sendersTo[key.Target] = list = [];
            list.Add(group);
        }
    }

    /// <summary>Where the sender posts when it is shown in <paramref name="page"/>.</summary>
    private IEnumerable<(string Target, FormSender Sender)> Targets(FormSender sender, string page)
    {
        var dir = PagePaths.Directory(page);
        if (sender.Url is not null)
        {
            if (Resolve(dir, sender.Url) is { } t) yield return (t, sender);
            yield break;
        }
        if (sender.Kind != FormSenderKind.Form) yield break;

        var refs = new[] { sender.FormName, sender.FormId }.Where(r => r is not null).ToHashSet(StringComparer.Ordinal);
        var actions = refs.Count == 0 ? [] : ActionsFor(page).Where(a => refs.Contains(a.FormRef)).ToList();
        if (actions.Count == 0)
        {
            // No action and no JS assignment: the form posts to the page itself (HTML rule), unless the page's JS sets some
            // action in a way not tied to this form (an alias, an event attribute) — then the address is not known.
            if (WritesActionSomewhere(page)) yield break;
            if (!IsInclude(page) && _known.ContainsKey(page)) yield return (Canonical(page), sender);
            yield break;
        }
        if (actions.Any(a => a.Url is null)) yield break;
        foreach (var a in actions)
            if (Resolve(dir, a.Url!) is { } t)
            {
                // The action's query keys are sent too (a hidden mode input moved into frm.action = "save.asp?mode=del").
                var query = FormFacts.QueryKeys(a.Url!).ToList();
                yield return (t, sender.Fields is null || query.Count == 0 ? sender
                    : sender with { Fields = [.. sender.Fields, .. query.Where(q => !sender.Fields.Contains(q))] });
            }
    }

    private bool WritesActionSomewhere(string page)
    {
        var files = WithIncludes(page);
        var scripts = files.SelectMany(f => _index.ScriptSources.TryGetValue(f, out var js) ? js : []);
        return files.Concat(scripts).Any(f => FactsOf(f).WritesAction);
    }

    /// <summary>A site page the address names, or null (external, server-built, an include, not in the index).</summary>
    private string? Resolve(string dir, string url)
    {
        if (url.Contains("<%", StringComparison.Ordinal)) return null;
        if (PagePaths.ResolveScriptSrc(dir, url.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)) is not { } rel) return null;
        if (IsInclude(rel) || IsScript(rel)) return null;
        return _known.TryGetValue(rel, out var k) ? k : null;
    }

    // ---- Mismatches ---------------------------------------------------------------------------------------------

    private static bool Sends(FormSenderGroup g, string key, FormReadSource source)
    {
        // A Form read is answered only by a POST body; an Any read also by a GET form and by query keys.
        if (source == FormReadSource.Form && !g.AnyPost) return false;
        foreach (var f in g.Fields!)
        {
            var query = f.StartsWith('?');
            if (query && source != FormReadSource.Any) continue;
            var name = query ? f[1..] : f;
            if (name.EndsWith('*') ? key.StartsWith(name[..^1], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private List<FormMismatch> Mismatches()
    {
        var result = new List<FormMismatch>();
        foreach (var (target, groups) in _sendersTo)
        {
            if (AllRead(target)) continue;
            var reads = ReadsOf(target).Where(r => r.Source != FormReadSource.QueryString).ToList();
            var readKeys = reads.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var read in reads.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
            {
                // Either kind of read counts as sent when any read of that key is answered.
                var sources = reads.Where(r => string.Equals(r.Key, read.Key, StringComparison.OrdinalIgnoreCase)).Select(r => r.Source).ToList();
                bool Answered(FormSenderGroup g) => sources.Any(s => Sends(g, read.Key, s));
                var weakest = sources.Contains(FormReadSource.Any) ? FormReadSource.Any : FormReadSource.Form;

                foreach (var g in groups.Where(g => g.Fields is not null))
                {
                    // A GET-only sender is not meant to feed Request.Form.
                    if (weakest == FormReadSource.Form && !g.AnyPost) continue;
                    if (Answered(g)) continue;
                    result.Add(new FormMismatch(FormMismatchKind.NotSent, target, read.Key, read.File, read.Line, g.File, g.Line, g.Kind,
                        Similar(g, readKeys), groups.Count));
                }
                // Only Request.Form reads: Request("k") can also come from links and redirects, which are not modelled. A cut index
                // may miss senders.
                if (!_index.Truncated && sources.Contains(FormReadSource.Form)
                    && groups.All(g => g.Fields is not null) && !groups.Any(Answered))
                    result.Add(new FormMismatch(FormMismatchKind.NobodySends, target, read.Key, read.File, read.Line, null, 0,
                        FormSenderKind.Form, null, groups.Count));
            }
        }
        return result;
    }

    /// <summary>
    /// Design §5.2: a key the sender never sent is not counted (it may belong to another mode). True when the file had a closed
    /// sender group to the target that sent the key.
    /// </summary>
    private bool SentBefore(string file, string target, string key) =>
        SendersTo(target).FirstOrDefault(g => string.Equals(g.File, file, StringComparison.OrdinalIgnoreCase)) is { Fields: { } fields }
        && fields.Any(f =>
        {
            var name = f.StartsWith('?') ? f[1..] : f;
            return name.EndsWith('*') ? key.StartsWith(name[..^1], StringComparison.OrdinalIgnoreCase)
                : string.Equals(name, key, StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>The one name the sender sends that the page does not read, if there is exactly one.</summary>
    private static string? Similar(FormSenderGroup g, HashSet<string> readKeys)
    {
        var unread = g.Fields!.Where(f => !f.StartsWith('?') && !f.EndsWith('*') && !readKeys.Contains(f)).ToList();
        return unread.Count == 1 ? unread[0] : null;
    }

    /// <summary>
    /// The mismatches <paramref name="after"/> has and <paramref name="before"/> does not, that the edit touched (design §5.2):
    /// a missing key of an edited sender, or a key nobody sends that an edited reader (the page or one of its includes) reads.
    /// </summary>
    public static IReadOnlyList<FormMismatch> NewMismatches(FormSite before, FormSite after, IReadOnlyCollection<string> edited)
    {
        var e = edited.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var old = before.Mismatches()
            .Select(m => (m.Kind, m.Target.ToLowerInvariant(), m.Key.ToLowerInvariant(), m.SenderFile?.ToLowerInvariant()))
            .ToHashSet();
        var fresh = after.Mismatches()
            .Where(m => !old.Contains((m.Kind, m.Target.ToLowerInvariant(), m.Key.ToLowerInvariant(), m.SenderFile?.ToLowerInvariant())))
            .Where(m => m.Kind == FormMismatchKind.NotSent
                ? e.Contains(m.SenderFile!) && before.SentBefore(m.SenderFile!, m.Target, m.Key)
                : after.WithIncludes(m.Target).Any(e.Contains))
            .ToList();
        var notSent = fresh.Where(m => m.Kind == FormMismatchKind.NotSent)
            .Select(m => (m.Target.ToLowerInvariant(), m.Key.ToLowerInvariant())).ToHashSet();
        return fresh
            .Where(m => m.Kind == FormMismatchKind.NotSent || !notSent.Contains((m.Target.ToLowerInvariant(), m.Key.ToLowerInvariant())))
            .OrderBy(m => m.Target, StringComparer.Ordinal).ThenBy(m => m.ReadLine).ThenBy(m => m.Key, StringComparer.Ordinal)
            .ThenBy(m => m.SenderFile, StringComparer.Ordinal)
            .ToList();
    }
}
