using System.Text.RegularExpressions;

namespace PellyCoder.Core.Asp;

public enum AspSegmentKind { Html, Code, Output, Directive, ServerScript, Include, Metadata }

/// <summary>
/// A page segment. Start/End are the 1-based positions of the first and last character of the whole segment
/// (delimiters included). <see cref="Text"/> is the inner text for Code, Output, ServerScript and Directive,
/// the path for Include and the raw text for Html and Metadata.
/// </summary>
public sealed record AspSegment(AspSegmentKind Kind, int StartLine, int StartColumn, int EndLine, int EndColumn, string Text);

public sealed record AspInclude(string Path, bool Virtual, int Line);

/// <summary><see cref="StartLine"/> includes comment lines directly above the declaration.</summary>
public sealed record AspProcedure(
    string Name, string? ClassName, string Keyword, IReadOnlyList<string> Parameters,
    int StartLine, int DeclLine, int EndLine, string Declaration);

public sealed record AspDeclaration(string Name, string Kind, int Line, string? Procedure);

public sealed record AspNameUse(string Name, int Line, bool IsAssignment, bool IsCall, string? Procedure);

/// <summary>A parse problem. <see cref="Message"/> never contains a line number, so problems compare by (Code, Message).</summary>
public sealed record AspProblem(int Line, int Column, string Code, string Message);

/// <summary>Classic ASP (VBScript) page: segments, statements, block pairs, procedures, declarations and name uses.</summary>
public sealed class AspPage
{
    private readonly string[] _lines;

    private AspPage(string textLf)
    {
        var lines = textLf.Split('\n');
        // A final line feed ends the last line; it does not start a new one.
        _lines = lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    public IReadOnlyList<AspSegment> Segments { get; private set; } = [];
    public IReadOnlyList<AspInclude> Includes { get; private set; } = [];
    public IReadOnlyList<VbStatement> Statements { get; private set; } = [];
    public IReadOnlyList<AspProcedure> Procedures { get; private set; } = [];
    public IReadOnlyList<AspDeclaration> Declarations { get; private set; } = [];
    public IReadOnlyList<AspNameUse> NameUses { get; private set; } = [];
    public IReadOnlyList<AspProblem> Problems { get; private set; } = [];
    public bool OptionExplicit { get; private set; }
    public int? CodePage { get; private set; }
    public string Language { get; private set; } = "VBScript";
    public bool HasServerJScript { get; private set; }
    public bool UsesDynamicCode { get; private set; }
    public IReadOnlySet<int> LockedLines { get; private set; } = new HashSet<int>();

    /// <summary>Text of a 1-based line without the line feed.</summary>
    public string LineText(int line) => _lines[line - 1];

    public static AspPage Parse(string textLf)
    {
        var page = new AspPage(textLf);
        var problems = new List<AspProblem>();
        var scan = new PageScanner(textLf, problems);
        scan.Run();

        page.Segments = scan.Segments;
        page.Includes = scan.Includes;
        page.HasServerJScript = scan.HasServerJScript;
        ReadDirectives(page, scan.Segments);
        page.LockedLines = scan.Segments
            .Where(s => s.Kind is AspSegmentKind.Directive or AspSegmentKind.Include or AspSegmentKind.Metadata)
            .SelectMany(s => Enumerable.Range(s.StartLine, s.EndLine - s.StartLine + 1))
            .ToHashSet();

        var statements = new List<VbStatement>();
        var outputs = new HashSet<VbStatement>(ReferenceEqualityComparer.Instance);
        if (IsVbScript(page.Language))
        {
            foreach (var chunk in scan.CodeChunks)
            {
                var chunkStatements = VbLexer.Statements([(chunk.Line, chunk.Column, chunk.Text)], problems);
                statements.AddRange(chunkStatements);
                if (chunk.IsOutput)
                    outputs.UnionWith(chunkStatements);
            }
        }
        page.Statements = statements;

        var analyzer = new StatementAnalyzer(page, problems);
        analyzer.Run(statements, outputs);
        page.Procedures = analyzer.Procedures;
        page.Declarations = analyzer.Declarations;
        page.NameUses = analyzer.NameUses;
        page.OptionExplicit = analyzer.OptionExplicit;
        page.UsesDynamicCode = analyzer.NameUses.Any(u =>
            u.Name.Equals("Execute", StringComparison.OrdinalIgnoreCase)
            || u.Name.Equals("ExecuteGlobal", StringComparison.OrdinalIgnoreCase)
            || u.Name.Equals("Eval", StringComparison.OrdinalIgnoreCase));

        HtmlTagBalance.Check(scan.Segments, problems);
        page.Problems = problems.OrderBy(p => p.Line).ThenBy(p => p.Column).ToArray();
        return page;
    }

    private static readonly Regex AttributePattern = new(
        """([\w-]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+))""", RegexOptions.CultureInvariant);

    internal static Dictionary<string, string> Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributePattern.Matches(text))
        {
            var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            result.TryAdd(m.Groups[1].Value, value);
        }
        return result;
    }

    private static void ReadDirectives(AspPage page, IReadOnlyList<AspSegment> segments)
    {
        foreach (var segment in segments.Where(s => s.Kind == AspSegmentKind.Directive))
        {
            var attributes = Attributes(segment.Text);
            if (attributes.TryGetValue("Language", out var language) && language.Length > 0)
                page.Language = language;
            if (attributes.TryGetValue("CodePage", out var codePage) && int.TryParse(codePage, out var number))
                page.CodePage = number;
        }
    }

    private static bool IsVbScript(string language) =>
        language.Equals("VBScript", StringComparison.OrdinalIgnoreCase)
        || language.Equals("VBS", StringComparison.OrdinalIgnoreCase);

    private sealed record CodeChunk(int Line, int Column, string Text, bool IsOutput);

    /// <summary>Splits page text into segments. As in IIS, <c>%&gt;</c> ends a block wherever it appears.</summary>
    private sealed class PageScanner(string text, List<AspProblem> problems)
    {
        private readonly List<int> _lineStarts = LineStarts(text);
        private int _htmlStart;

        public List<AspSegment> Segments { get; } = [];
        public List<AspInclude> Includes { get; } = [];
        public List<CodeChunk> CodeChunks { get; } = [];
        public bool HasServerJScript { get; private set; }

        public void Run()
        {
            var i = 0;
            while (i < text.Length)
            {
                if (text[i] != '<')
                {
                    i++;
                    continue;
                }
                var end = At(i, "<%") ? ScanAspBlock(i)
                    : At(i, "<!--") ? ScanSpecialComment(i)
                    : At(i, "<script") ? ScanServerScript(i)
                    : -1;
                if (end < 0)
                {
                    i++;
                    continue;
                }
                _htmlStart = end;
                i = end;
            }
            FlushHtml(text.Length);
        }

        private static List<int> LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (var k = 0; k < text.Length; k++)
                if (text[k] == '\n')
                    starts.Add(k + 1);
            return starts;
        }

        private (int Line, int Column) Pos(int offset)
        {
            var index = _lineStarts.BinarySearch(offset);
            if (index < 0)
                index = ~index - 1;
            return (index + 1, offset - _lineStarts[index] + 1);
        }

        private bool At(int offset, string token) =>
            string.Compare(text, offset, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;

        private void FlushHtml(int end)
        {
            if (end > _htmlStart)
                Add(AspSegmentKind.Html, _htmlStart, end, text[_htmlStart..end]);
        }

        private void Add(AspSegmentKind kind, int start, int end, string segmentText)
        {
            var (startLine, startColumn) = Pos(start);
            var (endLine, endColumn) = Pos(Math.Max(start, end - 1));
            Segments.Add(new AspSegment(kind, startLine, startColumn, endLine, endColumn, segmentText));
        }

        /// <summary>Returns the offset after the block's <c>%&gt;</c> (or the text end when unclosed).</summary>
        private int ScanAspBlock(int start)
        {
            FlushHtml(start);
            var marker = start + 2 < text.Length ? text[start + 2] : '\0';
            var kind = marker == '@' ? AspSegmentKind.Directive : marker == '=' ? AspSegmentKind.Output : AspSegmentKind.Code;
            var contentStart = kind == AspSegmentKind.Code ? start + 2 : start + 3;
            var close = FindAspClose(contentStart);
            int contentEnd, end;
            if (close < 0)
            {
                var (line, column) = Pos(start);
                problems.Add(new AspProblem(line, column, "ASP001", "`<%`가 닫히지 않았습니다"));
                contentEnd = end = text.Length;
            }
            else
            {
                contentEnd = close;
                end = close + 2;
            }
            var inner = text[Math.Min(contentStart, contentEnd)..contentEnd];
            Add(kind, start, end, inner);
            if (kind != AspSegmentKind.Directive)
                AddChunk(contentStart, inner, kind == AspSegmentKind.Output);
            return end;
        }

        /// <summary>Like IIS, the first <c>%&gt;</c> ends the block, even inside a string or comment.</summary>
        private int FindAspClose(int from) =>
            from >= text.Length ? -1 : text.IndexOf("%>", from, StringComparison.Ordinal);

        private void AddChunk(int offset, string chunkText, bool isOutput)
        {
            var (line, column) = Pos(offset);
            CodeChunks.Add(new CodeChunk(line, column, chunkText, isOutput));
        }

        /// <summary><c>&lt;!--#include …--&gt;</c> and <c>&lt;!--METADATA …--&gt;</c>; -1 for an ordinary comment.</summary>
        private int ScanSpecialComment(int start)
        {
            var k = start + 4;
            while (k < text.Length && char.IsWhiteSpace(text[k]))
                k++;
            var isInclude = At(k, "#include");
            if (!isInclude && !At(k, "METADATA"))
                return -1;
            var close = text.IndexOf("-->", k, StringComparison.Ordinal);
            if (close < 0)
                return -1;
            var end = close + 3;
            var raw = text[start..end];
            if (!isInclude)
            {
                FlushHtml(start);
                Add(AspSegmentKind.Metadata, start, end, raw);
                return end;
            }
            var attributes = Attributes(raw);
            var isVirtual = attributes.TryGetValue("virtual", out var path);
            if (!isVirtual && !attributes.TryGetValue("file", out path))
                return -1;
            FlushHtml(start);
            Add(AspSegmentKind.Include, start, end, path!);
            Includes.Add(new AspInclude(path!, isVirtual, Pos(start).Line));
            return end;
        }

        /// <summary><c>&lt;script runat="server"&gt;…&lt;/script&gt;</c>; -1 for a client script tag.</summary>
        private int ScanServerScript(int start)
        {
            var after = start + 7;
            if (after < text.Length && !char.IsWhiteSpace(text[after]) && text[after] != '>')
                return -1;
            var tagEnd = text.IndexOf('>', after);
            if (tagEnd < 0)
                return -1;
            var attributes = Attributes(text[after..tagEnd]);
            if (!attributes.TryGetValue("runat", out var runat) || !runat.Equals("server", StringComparison.OrdinalIgnoreCase))
                return -1;

            FlushHtml(start);
            var contentStart = tagEnd + 1;
            var closeTag = text.IndexOf("</script", contentStart, StringComparison.OrdinalIgnoreCase);
            int contentEnd, end;
            if (closeTag < 0)
            {
                var (line, column) = Pos(start);
                problems.Add(new AspProblem(line, column, "ASP002", "`<script runat=\"server\">`가 닫히지 않았습니다"));
                contentEnd = end = text.Length;
            }
            else
            {
                contentEnd = closeTag;
                var gt = text.IndexOf('>', closeTag);
                end = gt < 0 ? text.Length : gt + 1;
            }
            var inner = text[contentStart..contentEnd];
            Add(AspSegmentKind.ServerScript, start, end, inner);

            var language = attributes.GetValueOrDefault("language") ?? "";
            if (language.Equals("JScript", StringComparison.OrdinalIgnoreCase)
                || language.Equals("JavaScript", StringComparison.OrdinalIgnoreCase))
                HasServerJScript = true;
            else
                AddChunk(contentStart, inner, isOutput: false);
            return end;
        }
    }

    /// <summary>Counts block pairs across all code segments of the file and collects procedures, declarations and name uses.</summary>
    private sealed class StatementAnalyzer(AspPage page, List<AspProblem> problems)
    {
        private sealed class OpenBlock(string name, string closer, VbStatement statement)
        {
            public string Name { get; } = name;
            public string Closer { get; } = closer;
            public VbStatement Statement { get; } = statement;
            public ProcBuilder? Procedure { get; init; }
            public string? ClassName { get; init; }
        }

        private sealed class ProcBuilder(string name, string? className, string keyword, List<string> parameters, int declLine, string declaration)
        {
            public string Name { get; } = name;
            public string? ClassName { get; } = className;
            public string Scope { get; } = className is null ? name : className + "." + name;
            public int EndLine { get; set; }

            public AspProcedure Build(int startLine) =>
                new(Name, ClassName, keyword, parameters, startLine, declLine, EndLine, declaration);

            public int DeclLine => declLine;
        }

        private readonly List<OpenBlock> _stack = [];
        private readonly List<ProcBuilder> _procedures = [];
        private readonly List<AspDeclaration> _declarations = [];
        private readonly List<AspNameUse> _uses = [];

        public IReadOnlyList<AspProcedure> Procedures { get; private set; } = [];
        public IReadOnlyList<AspDeclaration> Declarations => _declarations;
        public IReadOnlyList<AspNameUse> NameUses => _uses;
        public bool OptionExplicit { get; private set; }

        /// <summary>Scope of names: <c>Proc</c>, <c>Class.Proc</c> for class methods, <c>Class</c> for class fields, null at module level.</summary>
        private string? CurrentScope => _stack.LastOrDefault(b => b.Procedure is not null)?.Procedure!.Scope ?? CurrentClass;
        private string? CurrentClass => _stack.LastOrDefault(b => b.ClassName is not null)?.ClassName;

        public void Run(IReadOnlyList<VbStatement> statements, HashSet<VbStatement> outputs)
        {
            foreach (var statement in statements)
            {
                if (outputs.Contains(statement))
                    CollectUses(statement.Tokens, new HashSet<int>(), CurrentScope, expressionOnly: true);
                else
                    Analyze(statement);
            }
            var lastLine = page._lines.Length;
            foreach (var open in _stack)
                Unclosed(open, lastLine);
            _stack.Clear();
            Procedures = _procedures.Select(p => p.Build(LeadingCommentStart(p.DeclLine))).ToArray();
        }

        private void Analyze(VbStatement statement)
        {
            var t = statement.Tokens;
            var excluded = new HashSet<int>();
            var modifiers = 0;
            while (modifiers < t.Count && (IsKw(t[modifiers], "Public") || IsKw(t[modifiers], "Private") || IsKw(t[modifiers], "Default")))
                modifiers++;
            var head = modifiers < t.Count ? t[modifiers] : null;
            var second = modifiers + 1 < t.Count ? t[modifiers + 1] : null;

            if (IsKw(t[0], "Option") && t.Count > 1 && IsKw(t[1], "Explicit"))
                OptionExplicit = true;

            if (head is not null && (IsKw(head, "Sub") || IsKw(head, "Function") || IsKw(head, "Property")))
                OpenProcedure(statement, modifiers, excluded);
            else if (head is not null && IsKw(head, "Class") && second?.Kind == VbTokenKind.Identifier)
            {
                excluded.Add(modifiers + 1);
                _declarations.Add(new AspDeclaration(second.Text, "Class", statement.StartLine, null));
                _stack.Add(new OpenBlock("Class", "End Class", statement) { ClassName = second.Text });
            }
            else if (head is not null && (IsKw(head, "Dim") || IsKw(head, "ReDim") || IsKw(head, "Const")))
                DeclareVariables(statement, modifiers + 1, Canonical(head.Text), excluded);
            else if (modifiers > 0 && head is not null)
                DeclareVariables(statement, modifiers, Canonical(t[0].Text), excluded);
            else if (modifiers == 0)
                CountBlock(statement);

            CollectUses(t, excluded, CurrentScope, expressionOnly: false);
        }

        private void CountBlock(VbStatement statement)
        {
            var t = statement.Tokens;
            var first = t[0];
            if (first.Kind != VbTokenKind.Keyword)
                return;
            var word = Canonical(first.Text);
            switch (word)
            {
                case "If" when IsKw(t[^1], "Then"):
                    Open("If", "End If", statement);
                    break;
                case "For":
                    Open(t.Count > 1 && IsKw(t[1], "Each") ? "For Each" : "For", "Next", statement);
                    break;
                case "Do":
                    Open("Do", "Loop", statement);
                    break;
                case "While":
                    Open("While", "Wend", statement);
                    break;
                case "Select":
                    Open("Select Case", "End Select", statement);
                    break;
                case "With":
                    Open("With", "End With", statement);
                    break;
                case "Next" or "Loop" or "Wend":
                    Close(word, statement);
                    break;
                case "End" when t.Count > 1 && t[1].Kind == VbTokenKind.Keyword:
                    var target = Canonical(t[1].Text);
                    if (target is "If" or "Select" or "With" or "Sub" or "Function" or "Property" or "Class")
                        Close("End " + target, statement);
                    break;
            }
        }

        private void Open(string name, string closer, VbStatement statement) =>
            _stack.Add(new OpenBlock(name, closer, statement));

        private void Close(string closer, VbStatement statement)
        {
            var index = _stack.FindLastIndex(b => b.Closer == closer);
            if (index < 0)
            {
                var first = statement.Tokens[0];
                problems.Add(new AspProblem(first.Line, first.Column, "ASP004", $"`{closer}`의 여는 짝이 없습니다"));
                return;
            }
            for (var k = _stack.Count - 1; k > index; k--)
                Unclosed(_stack[k], statement.StartLine);
            if (_stack[index].Procedure is { } proc)
                proc.EndLine = statement.EndLine;
            _stack.RemoveRange(index, _stack.Count - index);
        }

        private void Unclosed(OpenBlock open, int endLine)
        {
            var first = open.Statement.Tokens[0];
            var particle = open.Name is "While" or "Function" ? "이" : "가";
            problems.Add(new AspProblem(first.Line, first.Column, "ASP005", $"`{open.Name}`{particle} 닫히지 않았습니다"));
            if (open.Procedure is { } proc)
                proc.EndLine = endLine;
        }

        private void OpenProcedure(VbStatement statement, int at, HashSet<int> excluded)
        {
            var t = statement.Tokens;
            var keyword = Canonical(t[at].Text);
            var nameIndex = at + 1;
            if (keyword == "Property")
            {
                if (nameIndex < t.Count && (IsKw(t[nameIndex], "Get") || IsKw(t[nameIndex], "Let") || IsKw(t[nameIndex], "Set")))
                    keyword += " " + Canonical(t[nameIndex].Text);
                nameIndex++;
            }
            if (nameIndex >= t.Count)
                return;
            var name = t[nameIndex].Text;
            excluded.Add(nameIndex);
            var parameters = new List<string>();
            if (nameIndex + 1 < t.Count && t[nameIndex + 1].Kind == VbTokenKind.OpenParen)
            {
                var expectName = true;
                for (var k = nameIndex + 2; k < t.Count && t[k].Kind != VbTokenKind.CloseParen; k++)
                {
                    excluded.Add(k);
                    if (t[k].Kind == VbTokenKind.Comma)
                        expectName = true;
                    else if (expectName && t[k].Kind == VbTokenKind.Identifier)
                    {
                        parameters.Add(t[k].Text);
                        expectName = false;
                    }
                }
            }
            var proc = new ProcBuilder(name, CurrentClass, keyword, parameters, statement.StartLine, statement.Text);
            foreach (var parameter in parameters)
                _declarations.Add(new AspDeclaration(parameter, "Param", statement.StartLine, proc.Scope));
            _procedures.Add(proc);
            var blockName = keyword.Split(' ')[0];
            _stack.Add(new OpenBlock(blockName, "End " + blockName, statement) { Procedure = proc });
        }

        /// <summary>Declared names are the identifiers that start each comma group at parenthesis depth 0.</summary>
        private void DeclareVariables(VbStatement statement, int from, string kind, HashSet<int> excluded)
        {
            var t = statement.Tokens;
            if (kind == "ReDim" && from < t.Count && IsKw(t[from], "Preserve"))
                from++;
            if (kind is "Public" or "Private" && from < t.Count && IsKw(t[from], "Const"))
            {
                kind = "Const";
                from++;
            }
            var depth = 0;
            var expectName = true;
            for (var k = from; k < t.Count; k++)
            {
                var token = t[k];
                if (token.Kind == VbTokenKind.OpenParen)
                    depth++;
                else if (token.Kind == VbTokenKind.CloseParen)
                    depth--;
                else if (token.Kind == VbTokenKind.Comma && depth == 0)
                    expectName = true;
                else if (expectName && depth == 0 && token.Kind == VbTokenKind.Identifier)
                {
                    excluded.Add(k);
                    _declarations.Add(new AspDeclaration(token.Text, kind, token.Line, CurrentScope));
                    expectName = false;
                }
            }
        }

        /// <summary>
        /// Name uses, members after <c>.</c> excluded. A clause starts at the statement start or after
        /// <c>Then</c>/<c>Else</c>/<c>:</c> of a single-line If.
        /// </summary>
        private void CollectUses(IReadOnlyList<VbToken> t, HashSet<int> excluded, string? procedure, bool expressionOnly)
        {
            var singleLineIf = !expressionOnly && IsKw(t[0], "If") && !IsKw(t[^1], "Then");
            var clauseStart = 0;
            for (var k = 0; k <= t.Count; k++)
            {
                var boundary = k == t.Count
                    || (singleLineIf && (IsKw(t[k], "Then") || IsKw(t[k], "Else") || (t[k].Kind == VbTokenKind.Operator && t[k].Text == ":")));
                if (!boundary)
                    continue;
                CollectClause(t, clauseStart, k, excluded, procedure, expressionOnly);
                clauseStart = k + 1;
            }
        }

        private void CollectClause(IReadOnlyList<VbToken> t, int from, int to, HashSet<int> excluded, string? procedure, bool expressionOnly)
        {
            if (from >= to)
                return;
            var start = from;
            var isStatement = !expressionOnly;
            var assignTarget = false;
            var forcedCall = false;
            if (t[start].Kind == VbTokenKind.Keyword)
            {
                var word = Canonical(t[start].Text);
                if (word == "Call")
                {
                    forcedCall = true;
                    start++;
                }
                else if (word == "Set")
                {
                    assignTarget = true;
                    start++;
                }
                else if (word == "For")
                {
                    assignTarget = true;
                    start++;
                    if (start < to && IsKw(t[start], "Each"))
                        start++;
                }
                else
                    isStatement = false;
            }

            for (var k = from; k < to; k++)
            {
                if (t[k].Kind != VbTokenKind.Identifier || excluded.Contains(k) || (k > 0 && t[k - 1].Kind == VbTokenKind.Dot))
                    continue;
                var next = k + 1 < to ? t[k + 1] : null;
                bool isAssignment = false, isCall;
                if (isStatement && k == start)
                {
                    if (next?.Kind == VbTokenKind.Dot)
                        isCall = false;
                    else if (assignTarget || IsAssignedAt(t, k + 1, to))
                    {
                        isAssignment = true;
                        isCall = false;
                    }
                    else
                        isCall = true;
                    isCall |= forcedCall && next?.Kind != VbTokenKind.Dot;
                }
                else
                    isCall = next?.Kind == VbTokenKind.OpenParen;
                _uses.Add(new AspNameUse(t[k].Text, t[k].Line, isAssignment, isCall, procedure));
            }
        }

        /// <summary>True when <c>=</c> follows, optionally after one parenthesised index list.</summary>
        private static bool IsAssignedAt(IReadOnlyList<VbToken> t, int k, int to)
        {
            if (k < to && t[k].Kind == VbTokenKind.OpenParen)
            {
                var depth = 0;
                for (; k < to; k++)
                {
                    if (t[k].Kind == VbTokenKind.OpenParen)
                        depth++;
                    else if (t[k].Kind == VbTokenKind.CloseParen && --depth == 0)
                    {
                        k++;
                        break;
                    }
                }
            }
            return k < to && t[k].Kind == VbTokenKind.Operator && t[k].Text == "=";
        }

        private int LeadingCommentStart(int declLine)
        {
            var line = declLine;
            while (line > 1 && IsCommentLine(page.LineText(line - 1)))
                line--;
            return line;
        }

        private static bool IsCommentLine(string lineText)
        {
            var trimmed = lineText.TrimStart();
            if (trimmed.StartsWith('\''))
                return true;
            return trimmed.StartsWith("rem", StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3]));
        }

        private static bool IsKw(VbToken token, string keyword) =>
            token.Kind == VbTokenKind.Keyword && token.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

        private static string Canonical(string keyword) =>
            keyword.ToLowerInvariant() switch
            {
                "if" => "If", "for" => "For", "do" => "Do", "while" => "While", "select" => "Select",
                "with" => "With", "next" => "Next", "loop" => "Loop", "wend" => "Wend", "end" => "End",
                "sub" => "Sub", "function" => "Function", "property" => "Property", "class" => "Class",
                "get" => "Get", "let" => "Let", "set" => "Set", "call" => "Call", "dim" => "Dim",
                "redim" => "ReDim", "const" => "Const", "public" => "Public", "private" => "Private",
                _ => keyword,
            };
    }

    /// <summary>
    /// ASP006: one problem per surplus tag of a few structural tags, counted in HTML segments only. A closer
    /// matches the latest open tag of its name; messages carry no counts so problems compare as a multiset.
    /// </summary>
    private static class HtmlTagBalance
    {
        private static readonly string[] Tags = ["table", "form", "select", "div", "textarea", "script"];

        public static void Check(IReadOnlyList<AspSegment> segments, List<AspProblem> problems)
        {
            var open = Tags.ToDictionary(t => t, _ => new Stack<(int Line, int Column)>());
            foreach (var segment in segments.Where(s => s.Kind == AspSegmentKind.Html))
            {
                var text = segment.Text;
                int line = segment.StartLine, column = segment.StartColumn;
                for (var i = 0; i < text.Length; i++)
                {
                    if (text[i] == '<' && TagAt(text, i) is var (tag, closing))
                    {
                        if (!closing)
                            open[tag].Push((line, column));
                        else if (!open[tag].TryPop(out _))
                            problems.Add(new AspProblem(line, column, "ASP006", $"`<{tag}>` 여는 태그 없이 닫혔습니다"));
                    }
                    if (text[i] == '\n')
                    {
                        line++;
                        column = 1;
                    }
                    else
                        column++;
                }
            }
            foreach (var tag in Tags)
                foreach (var (line, column) in open[tag])
                    problems.Add(new AspProblem(line, column, "ASP006", $"`<{tag}>` 닫는 태그가 모자랍니다"));
        }

        private static (string Tag, bool Closing)? TagAt(string text, int i)
        {
            var k = i + 1;
            var closing = k < text.Length && text[k] == '/';
            if (closing)
                k++;
            foreach (var tag in Tags)
            {
                if (string.Compare(text, k, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) != 0)
                    continue;
                var after = k + tag.Length;
                if (after >= text.Length || char.IsWhiteSpace(text[after]) || text[after] is '>' or '/')
                    return (tag, closing);
            }
            return null;
        }
    }
}
