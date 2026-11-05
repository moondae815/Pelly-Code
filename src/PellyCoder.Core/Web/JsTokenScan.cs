using System.Text;
using System.Text.RegularExpressions;

namespace PellyCoder.Core.Web;

public sealed record JsScanProblem(int Line, string Code, string Message);

/// <summary>
/// Token-level fallback for JavaScript that Acornima cannot parse: bracket, string and comment balance,
/// and a brace-matched list of <c>function name(…) {…}</c> declarations.
/// </summary>
public static class JsTokenScan
{
    private sealed record Scan(List<JsScanProblem> Problems, char[] Code);

    public static IReadOnlyList<JsScanProblem> Problems(string textLf) =>
        Run(textLf).Problems.OrderBy(p => p.Line).ToList();

    private const string RegexPrefix = "(,=:[!&|?{};";

    private static readonly HashSet<string> RegexKeywords = new(StringComparer.Ordinal)
    {
        "return", "typeof", "case", "do", "else", "in", "of", "new", "delete", "void", "throw", "instanceof",
    };

    private static Scan Run(string text)
    {
        var problems = new List<JsScanProblem>();
        var code = text.ToCharArray();
        var stack = new Stack<(char Ch, int Line)>(); // '{' '(' '[' or '$' for a template ${ … }
        var line = 1;
        var prev = '\0'; // previous significant character in code
        var prevWord = ""; // the identifier/keyword just before, if the previous token was a word
        var i = 0;

        void Blank(int from, int to) { for (var k = from; k < to && k < code.Length; k++) if (code[k] != '\n') code[k] = ' '; }

        // Reads template text from i (just after ` or after the } closing a ${ ) up to the closing ` or ${.
        void Template(ref int pos, ref int ln)
        {
            var startLine = ln;
            var from = pos;
            while (pos < text.Length)
            {
                var c = text[pos];
                if (c == '\\') { if (pos + 1 < text.Length && text[pos + 1] == '\n') ln++; pos += 2; continue; }
                if (c == '\n') ln++;
                if (c == '`') { Blank(from, pos); pos++; prev = '`'; return; }
                if (c == '$' && pos + 1 < text.Length && text[pos + 1] == '{')
                {
                    Blank(from, pos);
                    stack.Push(('$', ln));
                    pos += 2; prev = '{';
                    return;
                }
                pos++;
            }
            Blank(from, pos);
            problems.Add(new JsScanProblem(startLine, "JS904", "닫히지 않은 템플릿 문자열이 있습니다."));
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var e = text.IndexOf('\n', i);
                if (e < 0) e = text.Length;
                Blank(i, e); i = e; continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = e < 0 ? text.Length : e + 2;
                if (e < 0) problems.Add(new JsScanProblem(line, "JS905", "닫히지 않은 /* */ 주석이 있습니다."));
                for (var k = i; k < end; k++) if (text[k] == '\n') line++;
                Blank(i, end); i = end; continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                var j = i + 1;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_' || text[j] == '$')) j++;
                prevWord = text.Substring(i, j - i);
                prev = text[j - 1];
                i = j; continue;
            }
            var kw = prevWord; prevWord = "";
            if (c == '"' || c == '\'')
            {
                var j = i + 1;
                var closed = false;
                while (j < text.Length && text[j] != '\n')
                {
                    if (text[j] == '\\') { j += 2; if (j - 1 < text.Length && text[j - 1] == '\n') { line++; } continue; }
                    if (text[j] == c) { closed = true; break; }
                    j++;
                }
                if (j > text.Length) j = text.Length;
                if (!closed) problems.Add(new JsScanProblem(line, "JS904", "닫히지 않은 문자열이 있습니다."));
                var end = closed ? j + 1 : j;
                Blank(i + 1, closed ? j : end);
                i = end; prev = c; continue;
            }
            if (c == '`') { i++; Template(ref i, ref line); continue; }
            if (c == '/' && (prev == '\0' || RegexPrefix.IndexOf(prev) >= 0 || (kw.Length > 0 && RegexKeywords.Contains(kw))))
            {
                var j = i + 1;
                var inClass = false;
                var closed = false;
                while (j < text.Length && text[j] != '\n')
                {
                    var d = text[j];
                    if (d == '\\') { j += 2; continue; }
                    if (d == '[') inClass = true;
                    else if (d == ']') inClass = false;
                    else if (d == '/' && !inClass) { closed = true; break; }
                    j++;
                }
                if (closed) { Blank(i + 1, j); i = j + 1; prev = '/'; continue; }
                // not a regex literal after all; treat as a division sign
            }
            switch (c)
            {
                case '{': case '(': case '[':
                    stack.Push((c, line)); break;
                case '}':
                    if (stack.Count > 0 && stack.Peek().Ch == '$') { stack.Pop(); i++; Template(ref i, ref line); continue; }
                    Close('{', '}', "JS901", "중괄호", ref line);
                    break;
                case ')': Close('(', ')', "JS902", "괄호", ref line); break;
                case ']': Close('[', ']', "JS903", "대괄호", ref line); break;
            }
            prev = c;
            i++;
        }

        foreach (var (ch, ln) in stack)
        {
            var (code2, name) = ch switch
            {
                '{' or '$' => ("JS901", "중괄호 {"),
                '(' => ("JS902", "괄호 ("),
                _ => ("JS903", "대괄호 ["),
            };
            problems.Add(new JsScanProblem(ln, code2, $"여는 {name}가 닫히지 않았습니다."));
        }
        return new Scan(problems, code);

        void Close(char open, char close, string pcode, string name, ref int ln)
        {
            if (stack.Count > 0 && stack.Peek().Ch == open) stack.Pop();
            else problems.Add(new JsScanProblem(ln, pcode, $"닫는 {name} {close}에 짝이 되는 여는 {name}가 없습니다."));
        }
    }

    private static readonly Regex FunctionHead =
        new(@"\bfunction\s+([A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);

    public static IReadOnlyList<JsFunction> FunctionDeclarations(string textLf)
    {
        var code = new string(Run(textLf).Code);
        var lines = textLf.Split('\n');
        var commentOnly = CommentOnlyLines(textLf);
        var starts = new List<int> { 0 };
        for (var k = 0; k < textLf.Length; k++) if (textLf[k] == '\n') starts.Add(k + 1);
        int LineOf(int idx) { var p = starts.BinarySearch(idx); return (p >= 0 ? p : ~p - 1) + 1; }

        var result = new List<JsFunction>();
        foreach (Match m in FunctionHead.Matches(code))
        {
            if (m.Index > 0 && (code[m.Index - 1] == '.' || code[m.Index - 1] == '$')) continue;
            var open = m.Index + m.Length - 1;
            var close = Match(code, open, '(', ')');
            if (close < 0) continue;
            var brace = close + 1;
            while (brace < code.Length && char.IsWhiteSpace(code[brace])) brace++;
            if (brace >= code.Length || code[brace] != '{') continue;
            var end = Match(code, brace, '{', '}');
            if (end < 0) continue;
            var parms = textLf.Substring(open + 1, close - open - 1).Split(',')
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            var declLine = LineOf(m.Index);
            var start = StartWithComments(commentOnly, declLine);
            result.Add(new JsFunction(m.Groups[1].Value, null, parms, start, declLine, LineOf(end), lines[declLine - 1].Trim()));
        }
        return result;
    }

    /// <summary>
    /// Per line (0-based): true when the line holds a comment and nothing else (blank lines are false).
    /// Comment text is blanked by the scan, so interior lines of a multi-line block comment count and a code
    /// line with a trailing comment does not.
    /// </summary>
    internal static bool[] CommentOnlyLines(string textLf)
    {
        var orig = textLf.Split('\n');
        var masked = new string(Run(textLf).Code).Split('\n');
        var r = new bool[orig.Length];
        for (var i = 0; i < orig.Length; i++)
            r[i] = orig[i].Trim().Length > 0 && masked[i].Trim().Length == 0;
        return r;
    }

    /// <summary>First line (1-based) of the comment block directly above <paramref name="declLine"/>.</summary>
    internal static int StartWithComments(bool[] commentOnly, int declLine)
    {
        var start = declLine;
        while (start > 1 && commentOnly[start - 2]) start--;
        return start;
    }

    private static int Match(string code, int openIdx, char open, char close)
    {
        var depth = 0;
        for (var i = openIdx; i < code.Length; i++)
        {
            if (code[i] == open) depth++;
            else if (code[i] == close && --depth == 0) return i;
        }
        return -1;
    }
}
