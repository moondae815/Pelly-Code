using System.Text;
using System.Text.RegularExpressions;

namespace PellyCoder.Core.Web;

/// <summary>One declaration. <paramref name="Property"/> is verbatim (<c>*zoom</c>, <c>_height</c> keep their hack prefix).</summary>
public sealed record CssDeclaration(string Property, string Value, int Line);

/// <summary>
/// One rule with a block. <paramref name="Selector"/> is the normalized head (selector list or <c>@media …</c>),
/// <paramref name="Container"/> the enclosing block heads joined with " &gt; " (null at top level).
/// </summary>
public sealed record CssRule(
    string Selector,
    IReadOnlyList<string> Selectors,
    string? Container,
    int StartLine,
    int EndLine,
    IReadOnlyList<CssDeclaration> Declarations,
    bool IsAtRule);

public sealed record CssProblem(int Line, int Column, string Code, string Message);

/// <summary>
/// A hand-written CSS parser on CSS Syntax Level 3 tokenization rules (strings, comments, unquoted url(),
/// bracket pairs, semicolons). It is tolerant of legacy IE hacks and reports structural problems only.
/// </summary>
public sealed class CssSheet
{
    private static readonly Regex NextDeclStart = new(@"^\s*[*_]?-?[A-Za-z][\w-]*\s*:", RegexOptions.Compiled);
    private static readonly Regex PropertyName = new(@"^[*_]?-{0,2}[A-Za-z_][\w-]*$", RegexOptions.Compiled);
    private static readonly Regex AtName = new(@"^@(?:-[A-Za-z]+-)?([A-Za-z][\w-]*)", RegexOptions.Compiled);
    private static readonly HashSet<string> GroupAtRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "media", "supports", "document", "layer", "container", "keyframes", "scope", "starting-style",
    };

    private sealed class RuleBuilder
    {
        public string Selector = "";
        public IReadOnlyList<string> Selectors = Array.Empty<string>();
        public string? Container;
        public int StartLine;
        public int EndLine;
        public bool IsAtRule;
        public List<CssDeclaration> Declarations = new();
    }

    private sealed record Block(int RuleIndex, string Head, bool Group, int Line, int Column);

    private readonly string[] _lines;
    private readonly List<RuleBuilder> _builders = new();
    private readonly List<CssProblem> _problems = new();
    private readonly List<CssDeclaration> _all = new();

    private CssSheet(string text)
    {
        _lines = text.Split('\n');
    }

    public IReadOnlyList<CssRule> Rules { get; private set; } = Array.Empty<CssRule>();
    public IReadOnlyList<CssProblem> Problems => _problems;
    public bool UsesIeHacks { get; private set; }
    public bool UsesVendorPrefixes { get; private set; }

    public string LineText(int line) => line >= 1 && line <= _lines.Length ? _lines[line - 1] : "";

    public static CssSheet Parse(string textLf)
    {
        var sheet = new CssSheet(textLf);
        sheet.Run(textLf);
        return sheet;
    }

    private void Problem(int line, int column, string code, string message) =>
        _problems.Add(new CssProblem(line, column, code, message));

    private void Run(string text)
    {
        var n = text.Length;
        var i = 0;
        var line = 1;
        var col = 1;

        var seg = new StringBuilder();
        var segStarted = false;
        var segLine = 0;
        var segCol = 0;
        var breaks = new List<(int Index, int Line)>();
        var parens = new List<(char Open, int Line, int Column, bool Expr)>();
        var blocks = new List<Block>();

        void Step()
        {
            if (text[i] == '\n') { line++; col = 1; } else col++;
            i++;
        }

        void ResetSegment()
        {
            seg.Clear();
            segStarted = false;
            breaks.Clear();
        }

        void Append(char c)
        {
            if (!segStarted)
            {
                if (char.IsWhiteSpace(c)) return;
                segStarted = true;
                segLine = line;
                segCol = col;
            }
            seg.Append(c);
            if (c == '\n' && parens.Count == 0) breaks.Add((seg.Length, line + 1));
        }

        void AppendSpace()
        {
            if (segStarted && seg.Length > 0 && !char.IsWhiteSpace(seg[^1])) seg.Append(' ');
        }

        bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '\\' || c > 127;

        // Emits the pending segment as one or more declarations of the innermost rule.
        void FlushDeclarations()
        {
            if (blocks.Count == 0) return;
            var s = seg.ToString();
            var piece = 0;
            var pieceLine = segLine;
            foreach (var (index, brLine) in breaks)
            {
                var end = s.IndexOf('\n', index);
                var next = end < 0 ? s[index..] : s[index..end];
                if (!NextDeclStart.IsMatch(next) || s.IndexOf(':', piece, index - piece) < 0) continue;
                var lead = next.Length - next.TrimStart().Length;
                Problem(brLine, lead + 1, "CSS006", "선언 끝에 `;`가 빠졌습니다");
                AddDeclaration(s[piece..index], pieceLine);
                piece = index + lead;
                pieceLine = brLine;
            }
            AddDeclaration(s[piece..], pieceLine);
        }

        void AddDeclaration(string piece, int declLine)
        {
            var colon = piece.IndexOf(':');
            if (colon <= 0) return;
            var prop = piece[..colon].Trim();
            if (!PropertyName.IsMatch(prop)) return;
            var decl = new CssDeclaration(prop, piece[(colon + 1)..].Trim(), declLine);
            _builders[blocks[^1].RuleIndex].Declarations.Add(decl);
            _all.Add(decl);
        }

        void EndStatement()
        {
            var t = seg.ToString().Trim();
            if (t.Length > 0)
            {
                if (blocks.Count == 0 || blocks[^1].Group)
                {
                    if (t[0] != '@') Problem(segLine, segCol, "CSS007", "규칙 밖에 선언이 있습니다");
                }
                else FlushDeclarations();
            }
            ResetSegment();
        }

        void RecoverParens()
        {
            foreach (var p in parens)
                Problem(p.Line, p.Column, "CSS005", $"`{p.Open}`가 닫히지 않았습니다");
            parens.Clear();
        }

        void OpenBlock()
        {
            var head = seg.ToString().Trim();
            var isAt = head.StartsWith('@');
            string selector;
            IReadOnlyList<string> selectors;
            if (isAt)
            {
                selector = Regex.Replace(head, @"\s+", " ");
                selectors = new[] { selector };
            }
            else
            {
                selectors = CssSelector.Split(head);
                selector = CssSelector.Normalize(head);
            }
            var group = false;
            if (isAt)
            {
                var m = AtName.Match(head);
                group = m.Success && GroupAtRules.Contains(m.Groups[1].Value);
            }
            _builders.Add(new RuleBuilder
            {
                Selector = selector,
                Selectors = selectors,
                Container = blocks.Count == 0 ? null : string.Join(" > ", blocks.Select(b => b.Head)),
                StartLine = segStarted ? segLine : line,
                EndLine = line,
                IsAtRule = isAt,
            });
            blocks.Add(new Block(_builders.Count - 1, selector, group, line, col));
            ResetSegment();
        }

        void CloseBlock()
        {
            if (blocks.Count == 0)
            {
                Problem(line, col, "CSS003", "`}`의 여는 짝이 없습니다");
                ResetSegment();
                return;
            }
            if (!blocks[^1].Group && seg.ToString().Trim().Length > 0) FlushDeclarations();
            _builders[blocks[^1].RuleIndex].EndLine = line;
            blocks.RemoveAt(blocks.Count - 1);
            ResetSegment();
        }

        while (i < n)
        {
            var c = text[i];

            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                var startLine = line;
                var startCol = col;
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = close < 0 ? n : close + 2;
                while (i < stop) Step();
                if (close < 0) Problem(startLine, startCol, "CSS001", "주석이 닫히지 않았습니다");
                else AppendSpace();
                continue;
            }

            if (c == '"' || c == '\'')
            {
                var startLine = line;
                var startCol = col;
                var closed = false;
                Append(c);
                Step();
                while (i < n)
                {
                    var d = text[i];
                    if (d == '\n') break; // a bad string ends at the newline, which is left to the main loop
                    if (d == '\\' && i + 1 < n)
                    {
                        if (text[i + 1] != '\n') { Append(d); Step(); Append(text[i]); Step(); continue; }
                        Step(); Step(); // escaped newline: line continuation
                        continue;
                    }
                    Append(d);
                    Step();
                    if (d == c) { closed = true; break; }
                }
                if (!closed) Problem(startLine, startCol, "CSS002", "문자열이 닫히지 않았습니다");
                continue;
            }

            if (c == '\\' && i + 1 < n)
            {
                Append(c); Step();
                Append(text[i]); Step();
                continue;
            }

            if ((c == 'u' || c == 'U') && i + 3 < n && string.Compare(text, i, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) == 0
                && (i == 0 || !IsIdentChar(text[i - 1])))
            {
                var j = i + 4;
                while (j < n && char.IsWhiteSpace(text[j])) j++;
                if (j < n && text[j] != '"' && text[j] != '\'')
                {
                    var urlLine = line;
                    var urlCol = col + 3;
                    var closed = false;
                    for (var k = 0; k < 4; k++) { Append(text[i]); Step(); }
                    while (i < n)
                    {
                        var d = text[i];
                        if (d == '\\' && i + 1 < n) { Append(d); Step(); Append(text[i]); Step(); continue; }
                        Append(d);
                        Step();
                        if (d == ')') { closed = true; break; }
                    }
                    if (!closed) Problem(urlLine, urlCol, "CSS005", "`(`가 닫히지 않았습니다");
                    continue;
                }
            }

            switch (c)
            {
                case '(':
                case '[':
                {
                    var t = seg.ToString();
                    var expr = c == '(' && t.EndsWith("expression", StringComparison.OrdinalIgnoreCase);
                    parens.Add((c, line, col, expr));
                    Append(c);
                    break;
                }
                case ')':
                case ']':
                    if (parens.Count == 0)
                        Problem(line, col, "CSS005", $"`{c}`의 여는 짝이 없습니다");
                    else
                    {
                        var open = parens[^1].Open;
                        if ((open == '(') != (c == ')'))
                            Problem(parens[^1].Line, parens[^1].Column, "CSS005", $"`{open}`가 닫히지 않았습니다");
                        parens.RemoveAt(parens.Count - 1);
                    }
                    Append(c);
                    break;
                case ';':
                    if (parens.Count > 0) Append(c);
                    else EndStatement();
                    break;
                case '{':
                    if (parens.Exists(p => p.Expr)) { Append(c); break; }
                    RecoverParens();
                    OpenBlock();
                    break;
                case '}':
                    if (parens.Exists(p => p.Expr)) { Append(c); break; }
                    RecoverParens();
                    CloseBlock();
                    break;
                case '<' when blocks.Count == 0 && !segStarted && string.CompareOrdinal(text, i, "<!--", 0, 4) == 0:
                    for (var k = 0; k < 4; k++) Step();
                    continue;
                case '-' when blocks.Count == 0 && !segStarted && string.CompareOrdinal(text, i, "-->", 0, 3) == 0:
                    for (var k = 0; k < 3; k++) Step();
                    continue;
                default:
                    Append(c);
                    break;
            }
            Step();
        }

        // End of input: whatever is still open is a problem.
        RecoverParens();
        var lastLine = text.EndsWith('\n') && line > 1 ? line - 1 : line;
        if (blocks.Count > 0)
        {
            if (!blocks[^1].Group && seg.ToString().Trim().Length > 0) FlushDeclarations();
            foreach (var b in blocks)
            {
                Problem(b.Line, b.Column, "CSS004", "`{`가 닫히지 않았습니다");
                _builders[b.RuleIndex].EndLine = lastLine;
            }
        }

        Rules = _builders
            .Select(b => new CssRule(b.Selector, b.Selectors, b.Container, b.StartLine, b.EndLine, b.Declarations, b.IsAtRule))
            .ToList();
        var ordered = _problems.OrderBy(p => p.Line).ThenBy(p => p.Column).ToList();
        _problems.Clear();
        _problems.AddRange(ordered);

        foreach (var d in _all)
        {
            var p = d.Property;
            if (p.StartsWith('*') || (p.StartsWith('_') && !p.StartsWith("__"))) UsesIeHacks = true;
            if (p.Equals("filter", StringComparison.OrdinalIgnoreCase)) UsesIeHacks = true;
            if (d.Value.Contains("expression(", StringComparison.OrdinalIgnoreCase)) UsesIeHacks = true;
            if (d.Value.TrimEnd().EndsWith("\\9", StringComparison.Ordinal)) UsesIeHacks = true;
            if (p.StartsWith("-webkit-", StringComparison.OrdinalIgnoreCase) || p.StartsWith("-moz-", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("-ms-", StringComparison.OrdinalIgnoreCase) || p.StartsWith("-o-", StringComparison.OrdinalIgnoreCase))
                UsesVendorPrefixes = true;
        }
    }
}
