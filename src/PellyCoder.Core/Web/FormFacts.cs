using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Web;

public enum FormReadSource { Form, Any, QueryString }

/// <summary>A server read of a request value by a literal key. <paramref name="Line"/> is the file line.</summary>
public sealed record FormRead(string Key, FormReadSource Source, int Line);

public enum FormSenderKind { Form, Ajax }

/// <summary>
/// Something that sends fields to a page: a <c>&lt;form&gt;</c> or a jQuery ajax call.
/// <list type="bullet">
/// <item><paramref name="Url"/> null with <paramref name="UrlUnknown"/> false: a form without action (the page itself, or a JS
/// assignment decides).</item>
/// <item><paramref name="Fields"/> null: open (the fields cannot all be known). A name ending in <c>*</c> is a prefix; a name
/// starting with <c>?</c> is a query key of the address.</item>
/// </list>
/// </summary>
public sealed record FormSender(
    FormSenderKind Kind, string? Url, bool UrlUnknown, bool Post, IReadOnlyList<string>? Fields, int Line,
    string? FormName = null, string? FormId = null);

/// <summary>A JS assignment of a form's action. <paramref name="Url"/> null: the address is not known.</summary>
public sealed record FormAction(string FormRef, string? Url, int Line);

/// <summary>The form facts of one file (W3 design §5.1, §5.4).</summary>
public sealed record FileFormFacts(
    IReadOnlyList<FormRead> Reads, bool AllRead, IReadOnlyList<FormSender> Senders, IReadOnlyList<FormAction> Actions)
{
    public static FileFormFacts Empty { get; } = new([], false, [], []);

    [JsonIgnore]
    public bool IsEmpty => Reads.Count == 0 && !AllRead && Senders.Count == 0 && Actions.Count == 0;
}

/// <summary>Reads form facts from one ASP/HTML page or one <c>.js</c> file. Only what is certain is kept (design §5.1).</summary>
public static class FormFacts
{
    private static readonly Regex FormOpen = new(@"<form(?![\w-])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FormClose = new(@"</form\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FieldOpen = new(
        @"<(?:input|select|textarea|button)(?![\w-])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Include = new(@"<!--\s*#include", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A value in "" (inside a VBScript string), " , ' or without quotes.
    private const string Value = @"(?:""""(?<v>(?:(?!"""").)*)""""|""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+))";
    private static readonly Regex FormAttribute = new(
        @"(?<![\w-])(?<k>action|method|name|id)\s*=\s*" + Value, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameAttribute = new(@"(?<![\w-])name\s*=\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameValue = new(@"\G" + Value, RegexOptions.Compiled);
    private static readonly Regex VbConcatenation = new(@"\G""{1,3}\s*&", RegexOptions.Compiled);

    private static readonly Regex AjaxCall = new(
        @"(?<![\w$.])(?:\$|jQuery)\s*\.\s*(?<m>ajax|post|get)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ActionProperty = new(
        @"(?<![\w$.])(?:document\s*\.\s*)?(?<f>[A-Za-z_$][\w$]*)\s*\.\s*action\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex ActionForms = new(
        @"(?<![\w$.])document\s*\.\s*forms\s*\[\s*(?<q>[""'])(?<f>[^""']*)\k<q>\s*\]\s*\.\s*action\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex ActionById = new(
        @"(?<![\w$.])document\s*\.\s*getElementById\s*\(\s*(?<q>[""'])(?<f>[^""']*)\k<q>\s*\)\s*\.\s*action\s*=(?!=)",
        RegexOptions.Compiled);
    private static readonly Regex ActionAttr = new(
        @"(?<![\w$.])(?:\$|jQuery)\s*\(\s*(?<q>[""'])(?<f>[^""']*)\k<q>\s*\)\s*\.\s*attr\s*\(\s*(?<q2>[""'])(?<a>[^""']*)\k<q2>\s*,",
        RegexOptions.Compiled);
    private static readonly Regex FieldsAdded = new(
        @"append\s*\(|appendChild\s*\(|\.html\s*\(|\.innerHTML", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"^[A-Za-z_$][\w$]*$", RegexOptions.Compiled);
    private static readonly Regex Callback = new(@"^(?:function\b|\(?[\w$\s,]*\)?\s*=>)", RegexOptions.Compiled);
    private static readonly Regex QueryKey = new(@"^[\w.\-\[\]]+$", RegexOptions.Compiled);

    public static FileFormFacts OfPage(AspPage page, string textLf)
    {
        var (reads, allRead) = Reads(page);
        var scripts = new List<string>();
        for (var i = 0; i < page.ClientBlocks.Count; i++)
            if (PageBlockView.IsJavaScriptBlock(page.ClientBlocks[i]))
                scripts.Add(PageBlockView.Build(page, textLf, i).Text);

        var senders = Forms(page, textLf, scripts).ToList();
        var actions = new List<FormAction>();
        foreach (var script in scripts)
        {
            senders.AddRange(Ajax(script));
            actions.AddRange(Actions(script));
        }
        return Facts(reads, allRead, senders.OrderBy(s => s.Line).ToList(), actions.OrderBy(a => a.Line).ToList());
    }

    public static FileFormFacts OfScript(string textLf) => Facts([], false, Ajax(textLf), Actions(textLf));

    private static FileFormFacts Facts(
        IReadOnlyList<FormRead> reads, bool allRead, IReadOnlyList<FormSender> senders, IReadOnlyList<FormAction> actions)
    {
        var facts = new FileFormFacts(reads, allRead, senders, actions);
        return facts.IsEmpty ? FileFormFacts.Empty : facts;
    }

    // ---- Reads -------------------------------------------------------------------------------------------------

    /// <summary><c>Request.Form("k")</c>, <c>Request("k")</c>, <c>Request.QueryString("k")</c> in the server code (tokens, so comments are gone).</summary>
    private static (IReadOnlyList<FormRead> Reads, bool AllRead) Reads(AspPage page)
    {
        var reads = new List<FormRead>();
        var allRead = false;
        foreach (var statement in page.Statements)
        {
            var t = statement.Tokens;
            for (var i = 0; i < t.Count; i++)
            {
                if (!IsWord(t[i], "Request") || (i > 0 && t[i - 1].Kind == VbTokenKind.Dot)) continue;
                var j = i + 1;
                var source = FormReadSource.Any;
                if (j + 1 < t.Count && t[j].Kind == VbTokenKind.Dot)
                {
                    if (IsWord(t[j + 1], "Form")) source = FormReadSource.Form;
                    else if (IsWord(t[j + 1], "QueryString")) source = FormReadSource.QueryString;
                    else continue;
                    j += 2;
                }
                if (j >= t.Count || t[j].Kind != VbTokenKind.OpenParen)
                {
                    // Request.Form as a collection: For Each … In Request.Form, Request.Form.Count.
                    if (source == FormReadSource.Form) allRead = true;
                    continue;
                }
                if (j + 2 < t.Count && t[j + 1].Kind == VbTokenKind.String && t[j + 2].Kind == VbTokenKind.CloseParen)
                {
                    var key = t[j + 1].Text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
                    if (key.Length > 0) reads.Add(new FormRead(key, source, t[j + 1].Line));
                }
                else if (source != FormReadSource.QueryString)
                    allRead = true;
            }
        }
        return (reads, allRead);
    }

    private static bool IsWord(VbToken token, string word) =>
        token.Kind is VbTokenKind.Identifier or VbTokenKind.Keyword && token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    // ---- Forms -------------------------------------------------------------------------------------------------

    private static IEnumerable<FormSender> Forms(AspPage page, string text, IReadOnlyList<string> scripts)
    {
        var starts = LineStarts(text);
        var procedures = page.Procedures.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Match open in FormOpen.Matches(text))
        {
            var tagEnd = TagEnd(text, open.Index);
            var tag = text[open.Index..Math.Min(text.Length, tagEnd + 1)];
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in FormAttribute.Matches(tag))
                attributes.TryAdd(a.Groups["k"].Value, a.Groups["v"].Value);

            var close = FormClose.Match(text, Math.Min(text.Length, tagEnd + 1));
            var bodyEnd = close.Success ? close.Index : text.Length;
            var firstLine = LineAt(starts, open.Index);
            var lastLine = LineAt(starts, Math.Max(open.Index, bodyEnd - 1));

            string? url = null;
            var unknown = false;
            if (attributes.TryGetValue("action", out var action) && action.Trim().Length > 0)
            {
                if (action.Contains("<%", StringComparison.Ordinal) || action.Contains('"')) unknown = true;
                else url = action.Trim();
            }
            var post = attributes.TryGetValue("method", out var method) && method.Trim().Equals("post", StringComparison.OrdinalIgnoreCase);
            var name = attributes.GetValueOrDefault("name");
            var id = attributes.GetValueOrDefault("id");

            var fields = Fields(text, tagEnd + 1, bodyEnd);
            if (fields is not null && Opened(page, text, tagEnd + 1, bodyEnd, firstLine, lastLine, procedures, scripts, name, id))
                fields = null;
            if (fields is not null && url is not null) fields = [.. fields, .. QueryKeys(url).Where(k => !fields.Contains(k))];

            yield return new FormSender(FormSenderKind.Form, url, unknown, post, fields, firstLine,
                NullIfEmpty(name), NullIfEmpty(id));
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) || s.Contains("<%", StringComparison.Ordinal) ? null : s.Trim();

    /// <summary>The field names in [from, to), or null when a name is built by VBScript concatenation.</summary>
    private static List<string>? Fields(string text, int from, int to)
    {
        var fields = new List<string>();
        var m = FieldOpen.Match(text, from);
        while (m.Success && m.Index < to)
        {
            var end = Math.Min(TagEnd(text, m.Index), to);
            var tag = text[m.Index..end];
            var n = NameAttribute.Match(tag);
            if (n.Success)
            {
                var at = n.Index + n.Length;
                if (VbConcatenation.IsMatch(tag, at)) return null;
                var v = NameValue.Match(tag, at);
                if (v.Success)
                {
                    var value = v.Groups["v"].Value;
                    if (value.Contains('"')) return null;
                    var server = value.IndexOf("<%", StringComparison.Ordinal);
                    if (server >= 0) value = value[..server] + "*";
                    if (value.Length > 0 && !fields.Contains(value)) fields.Add(value);
                }
            }
            m = FieldOpen.Match(text, Math.Max(end, m.Index + 1));
        }
        return fields;
    }

    /// <summary>Whether something in the form, or page JS, may add fields the text does not show.</summary>
    private static bool Opened(AspPage page, string text, int from, int to, int firstLine, int lastLine,
        HashSet<string> procedures, IReadOnlyList<string> scripts, string? name, string? id)
    {
        if (Include.Match(text, from) is { Success: true } inc && inc.Index < to) return true;

        foreach (var statement in page.Statements.Where(s => s.StartLine >= firstLine && s.EndLine <= lastLine))
        {
            var t = statement.Tokens;
            if (t.Count > 0 && IsWord(t[0], "Call")) return true;
            for (var i = 0; i < t.Count; i++)
                if (t[i].Kind == VbTokenKind.Identifier && procedures.Contains(t[i].Text) && (i == 0 || t[i - 1].Kind != VbTokenKind.Dot))
                    return true;
        }

        var refs = new[] { name, id }.Where(r => !string.IsNullOrWhiteSpace(r) && Identifier.IsMatch(r!.Trim())).ToList();
        foreach (var r in refs)
        {
            var word = new Regex(@"(?<![\w$-])" + Regex.Escape(r!.Trim()) + @"(?![\w$-])");
            foreach (var script in scripts)
                foreach (var line in script.Split('\n'))
                    if (FieldsAdded.IsMatch(line) && word.IsMatch(line))
                        return true;
        }
        return false;
    }

    /// <summary>The index of the <c>&gt;</c> closing the tag opened at <paramref name="start"/>, skipping <c>&lt;% %&gt;</c>.</summary>
    private static int TagEnd(string text, int start)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '<' && i + 1 < text.Length && text[i + 1] == '%')
            {
                var close = text.IndexOf("%>", i + 2, StringComparison.Ordinal);
                if (close < 0) return text.Length;
                i = close + 2;
                continue;
            }
            if (text[i] == '>') return i;
            i++;
        }
        return text.Length;
    }

    /// <summary>The query keys of an address, each as <c>?key</c>.</summary>
    internal static IEnumerable<string> QueryKeys(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0) yield break;
        var query = url[(q + 1)..];
        var hash = query.IndexOf('#');
        if (hash >= 0) query = query[..hash];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in query.Split('&'))
        {
            var part = raw.StartsWith("amp;", StringComparison.OrdinalIgnoreCase) ? raw[4..] : raw;
            var eq = part.IndexOf('=');
            var key = (eq >= 0 ? part[..eq] : part).Trim();
            if (key.Length > 0 && QueryKey.IsMatch(key) && seen.Add(key)) yield return "?" + key;
        }
    }

    // ---- Ajax ----------------------------------------------------------------------------------------------------

    private static List<FormSender> Ajax(string text)
    {
        var mask = JsTokenScan.Mask(text);
        var starts = LineStarts(text);
        var result = new List<FormSender>();
        foreach (Match m in AjaxCall.Matches(mask))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchClose(mask, open);
            if (close < 0) continue;
            var args = SplitTop(mask, open + 1, close);
            var line = LineAt(starts, m.Index);
            var kind = m.Groups["m"].Value;
            var defaultPost = kind == "post";

            (int S, int E)? settings = null;
            (int S, int E)? urlArg = null;
            (int S, int E)? dataArg = null;
            if (args.Count > 0 && StartsWith(mask, args[0], '{')) settings = args[0];
            else
            {
                if (args.Count > 0) urlArg = args[0];
                if (kind == "ajax") { if (args.Count > 1 && StartsWith(mask, args[1], '{')) settings = args[1]; }
                else if (args.Count > 1) dataArg = args[1];
            }

            string? url = null;
            var unknown = urlArg is null;
            var post = defaultPost;
            IReadOnlyList<string>? fields = [];
            if (urlArg is { } u) (url, unknown) = Address(text, mask, u.S, u.E, stripQueryOnConcat: false);
            if (settings is { } st)
            {
                var props = Properties(text, mask, st);
                if (props is null) { fields = null; }
                else
                {
                    if (props.TryGetValue("url", out var pu)) (url, unknown) = Address(text, mask, pu.S, pu.E, stripQueryOnConcat: false);
                    else if (urlArg is null) unknown = true;
                    var methodProp = props.TryGetValue("type", out var pt) ? pt : props.TryGetValue("method", out var pm) ? pm : ((int, int)?)null;
                    if (methodProp is { } mp)
                    {
                        if (Literal(text, mask, mp.Item1, mp.Item2) is { } verb) post = verb.Trim().Equals("post", StringComparison.OrdinalIgnoreCase);
                        else fields = null; // the method is not known, so it cannot be paired with certainty
                    }
                    if (props.TryGetValue("data", out var pd)) dataArg = pd;
                }
            }
            if (fields is not null && dataArg is { } d)
            {
                var (s, e) = Trim(mask, d.S, d.E);
                var expr = text[s..e];
                if (s < e && mask[s] == '{') fields = ObjectKeys(text, mask, s, MatchClose(mask, s));
                else if (Callback.IsMatch(expr)) fields = [];
                else fields = null;
            }
            if (fields is not null && url is not null) fields = [.. fields, .. QueryKeys(url).Where(k => !fields.Contains(k))];
            if (unknown) url = null;
            result.Add(new FormSender(FormSenderKind.Ajax, url, unknown, post, fields, line));
        }
        return result;
    }

    private static bool StartsWith(string mask, (int S, int E) range, char c)
    {
        var (s, e) = Trim(mask, range.S, range.E);
        return s < e && mask[s] == c;
    }

    /// <summary>The properties of the object literal in <paramref name="range"/>, or null when one cannot be read (spread, computed key).</summary>
    private static Dictionary<string, (int S, int E)>? Properties(string text, string mask, (int S, int E) range)
    {
        var (s, _) = Trim(mask, range.S, range.E);
        var close = MatchClose(mask, s);
        if (close < 0) return null;
        var result = new Dictionary<string, (int S, int E)>(StringComparer.Ordinal);
        foreach (var (ps, pe) in SplitTop(mask, s + 1, close))
        {
            var (a, b) = Trim(mask, ps, pe);
            if (a >= b) continue;
            var colon = IndexAtDepth0(mask, a, b, ':');
            if (colon < 0) return null;
            if (Key(text, mask, a, colon) is not { } key) return null;
            result.TryAdd(key, (colon + 1, b));
        }
        return result;
    }

    private static List<string>? ObjectKeys(string text, string mask, int open, int close)
    {
        if (close < 0) return null;
        var keys = new List<string>();
        foreach (var (ps, pe) in SplitTop(mask, open + 1, close))
        {
            var (a, b) = Trim(mask, ps, pe);
            if (a >= b) continue;
            var colon = IndexAtDepth0(mask, a, b, ':');
            var key = colon < 0
                ? (Identifier.IsMatch(text[a..b]) ? text[a..b] : null) // shorthand { a }
                : Key(text, mask, a, colon);
            if (key is null) return null;
            if (!keys.Contains(key)) keys.Add(key);
        }
        return keys;
    }

    private static string? Key(string text, string mask, int from, int to)
    {
        var (a, b) = Trim(mask, from, to);
        if (a >= b) return null;
        var raw = text[a..b];
        if ((mask[a] == '"' || mask[a] == '\'') && b - a >= 2 && mask[b - 1] == mask[a]) return raw[1..^1];
        return Identifier.IsMatch(raw) ? raw : null;
    }

    // ---- Action assignments -----------------------------------------------------------------------------------

    private static List<FormAction> Actions(string text)
    {
        var mask = JsTokenScan.Mask(text);
        var starts = LineStarts(text);
        var result = new List<FormAction>();
        void Add(string formRef, int at, int valueStart)
        {
            var end = ExpressionEnd(mask, valueStart);
            var (url, unknown) = Address(text, mask, valueStart, end, stripQueryOnConcat: true);
            result.Add(new FormAction(formRef, unknown ? null : url, LineAt(starts, at)));
        }

        foreach (Match m in ActionProperty.Matches(mask))
            Add(m.Groups["f"].Value, m.Index, m.Index + m.Length);
        foreach (var regex in new[] { ActionForms, ActionById })
            foreach (Match m in regex.Matches(mask))
            {
                var f = text.Substring(m.Groups["f"].Index, m.Groups["f"].Length).Trim();
                if (f.Length > 0) Add(f, m.Index, m.Index + m.Length);
            }
        foreach (Match m in ActionAttr.Matches(mask))
        {
            var f = text.Substring(m.Groups["f"].Index, m.Groups["f"].Length).Trim();
            var a = text.Substring(m.Groups["a"].Index, m.Groups["a"].Length).Trim();
            if (!a.Equals("action", StringComparison.OrdinalIgnoreCase) || !f.StartsWith('#') || !Identifier.IsMatch(f[1..])) continue;
            Add(f[1..], m.Index, m.Index + m.Length);
        }
        return result.OrderBy(a => a.Line).DistinctBy(a => (a.FormRef, a.Url, a.Line)).ToList();
    }

    /// <summary>Where an assigned value ends: <c>;</c>, a line break, a comma or an unmatched closer at depth 0.</summary>
    private static int ExpressionEnd(string mask, int from)
    {
        var depth = 0;
        for (var i = from; i < mask.Length; i++)
        {
            var c = mask[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (depth == 0) return i; depth--; }
            else if (depth == 0 && c is ';' or '\n' or ',') return i;
        }
        return mask.Length;
    }

    // ---- Shared ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The address in [from, to): a string literal, or a literal holding <c>?</c> followed by <c>+ …</c> (query concatenation).
    /// Anything else is unknown.
    /// </summary>
    private static (string? Url, bool Unknown) Address(string text, string mask, int from, int to, bool stripQueryOnConcat)
    {
        var (s, e) = Trim(mask, from, to);
        if (s >= e || (mask[s] != '"' && mask[s] != '\'')) return (null, true);
        var closeQuote = mask.IndexOf(mask[s], s + 1);
        if (closeQuote < 0 || closeQuote >= e) return (null, true);
        var literal = text[(s + 1)..closeQuote];
        if (literal.Contains("<%", StringComparison.Ordinal) || Regex.IsMatch(literal, "__asp[0-9]+__")) return (null, true);
        var rest = text[(closeQuote + 1)..e].Trim();
        if (rest.Length == 0) return literal.Trim().Length == 0 ? (null, true) : (literal.Trim(), false);
        if (rest.StartsWith('+') && literal.Contains('?'))
            return (stripQueryOnConcat ? literal[..literal.IndexOf('?')].Trim() : literal.Trim(), false);
        return (null, true);
    }

    private static string? Literal(string text, string mask, int from, int to)
    {
        var (s, e) = Trim(mask, from, to);
        if (s >= e || (mask[s] != '"' && mask[s] != '\'') || mask[e - 1] != mask[s] || e - s < 2) return null;
        return text[(s + 1)..(e - 1)];
    }

    private static (int S, int E) Trim(string mask, int from, int to)
    {
        while (from < to && char.IsWhiteSpace(mask[from])) from++;
        while (to > from && char.IsWhiteSpace(mask[to - 1])) to--;
        return (from, to);
    }

    private static int MatchClose(string mask, int open)
    {
        if (open < 0 || open >= mask.Length) return -1;
        var depth = 0;
        for (var i = open; i < mask.Length; i++)
        {
            var c = mask[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static List<(int S, int E)> SplitTop(string mask, int from, int to)
    {
        var result = new List<(int, int)>();
        var depth = 0;
        var start = from;
        for (var i = from; i < to; i++)
        {
            var c = mask[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) { result.Add((start, i)); start = i + 1; }
        }
        if (Trim(mask, start, to) is var (a, b) && a < b) result.Add((start, to));
        return result;
    }

    private static int IndexAtDepth0(string mask, int from, int to, char wanted)
    {
        var depth = 0;
        for (var i = from; i < to; i++)
        {
            var c = mask[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == wanted && depth == 0) return i;
        }
        return -1;
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    private static int LineAt(List<int> starts, int index)
    {
        var found = starts.BinarySearch(index);
        return found >= 0 ? found + 1 : ~found;
    }
}
