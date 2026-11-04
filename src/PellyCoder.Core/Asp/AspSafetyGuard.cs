using System.Text.RegularExpressions;

namespace PellyCoder.Core.Asp;

/// <summary>A safety rule hit. <see cref="Rule"/> is one of the rule IDs in <see cref="AspSafetyGuard"/>.</summary>
public sealed record AspSafetyFinding(string Rule, int Line, string Statement);

/// <summary>
/// Token-level safety rules for VBScript statements: shell objects, dynamic code, <c>On Error Resume Next</c> and
/// SQL text concatenated with unwrapped <c>Request</c> values. Each rule is reported at most once per statement.
/// </summary>
public static class AspSafetyGuard
{
    public const string ShellObject = "ShellObject";
    public const string DynamicCode = "DynamicCode";
    public const string OnErrorResumeNext = "OnErrorResumeNext";
    public const string SqlConcatRequest = "SqlConcatRequest";

    private const string FileSystemObject = "Scripting.FileSystemObject";

    private static readonly HashSet<string> ShellProgIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "WScript.Shell", "Shell.Application", "WScript.Network",
    };

    private static readonly HashSet<string> DynamicCodeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Execute", "ExecuteGlobal", "Eval",
    };

    private static readonly string[] DefaultWrappers = ["CLng", "CInt", "CDbl", "CBool", "CDate"];

    private static readonly HashSet<string> RequestCollections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Form", "QueryString", "Cookies",
    };

    private static readonly Regex SqlWord = new(
        @"\b(?:SELECT|INSERT|UPDATE|DELETE|EXEC|EXECUTE|MERGE)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<AspSafetyFinding> Findings(AspPage page, IReadOnlyList<string> sanitizers) =>
        Collect(page, sanitizers, includeFileSystemObject: true).Select(f => f.Finding).ToArray();

    /// <summary>
    /// Findings whose (Rule, normalized statement) count grew beyond the original, as many as it grew, with the
    /// patched page's line numbers (the later occurrences). <c>Scripting.FileSystemObject</c> is not a finding when
    /// the original mentions it anywhere.
    /// </summary>
    public static IReadOnlyList<AspSafetyFinding> NewFindings(AspPage original, AspPage patched, IReadOnlyList<string> sanitizers)
    {
        var includeFso = !original.Segments.Any(s => s.Text.Contains(FileSystemObject, StringComparison.OrdinalIgnoreCase));
        var baseline = Collect(original, sanitizers, includeFso)
            .GroupBy(f => (f.Finding.Rule, f.Key)).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<AspSafetyFinding>();
        foreach (var group in Collect(patched, sanitizers, includeFso).GroupBy(f => (f.Finding.Rule, f.Key)))
        {
            var extra = group.Count() - baseline.GetValueOrDefault(group.Key);
            if (extra > 0)
                result.AddRange(group.Select(f => f.Finding).OrderBy(f => f.Line).TakeLast(extra));
        }
        return result.OrderBy(f => f.Line).ThenBy(f => f.Rule, StringComparer.Ordinal).ToArray();
    }

    private sealed record Keyed(AspSafetyFinding Finding, string Key);

    private static List<Keyed> Collect(AspPage page, IReadOnlyList<string> sanitizers, bool includeFileSystemObject)
    {
        var wrappers = new HashSet<string>(DefaultWrappers.Concat(sanitizers), StringComparer.OrdinalIgnoreCase);
        var scopes = page.Statements.Select(s => ScopeOf(page, s)).ToArray();
        var taintedByScope = new Dictionary<int, HashSet<string>>();
        for (var i = 0; i < page.Statements.Count; i++)
        {
            if (!taintedByScope.TryGetValue(scopes[i], out var set))
                taintedByScope[scopes[i]] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectTaintedAssignments(page.Statements[i].Tokens, set);
        }

        var result = new List<Keyed>();
        for (var i = 0; i < page.Statements.Count; i++)
        {
            var statement = page.Statements[i];
            var key = string.Join(" ", statement.Tokens.Select(t => t.Text)).ToLowerInvariant();
            void Add(string rule, int line) =>
                result.Add(new Keyed(new AspSafetyFinding(rule, line, statement.Text.Trim()), key));

            if (ShellObjectLine(statement.Tokens, includeFileSystemObject) is { } shellLine)
                Add(ShellObject, shellLine);
            if (DynamicCodeLine(statement.Tokens) is { } dynamicLine)
                Add(DynamicCode, dynamicLine);
            if (OnErrorResumeNextLine(statement.Tokens) is { } errorLine)
                Add(OnErrorResumeNext, errorLine);
            var analyzer = new ConcatAnalyzer(statement.Tokens, wrappers, taintedByScope[scopes[i]]);
            if (analyzer.FindSqlConcat() is { } sqlLine)
                Add(SqlConcatRequest, sqlLine);
        }
        return result;
    }

    /// <summary>Index of the innermost procedure holding the statement, or -1 for page-level code.</summary>
    private static int ScopeOf(AspPage page, VbStatement statement)
    {
        var scope = -1;
        for (var p = 0; p < page.Procedures.Count; p++)
        {
            var procedure = page.Procedures[p];
            if (statement.StartLine >= procedure.DeclLine && statement.StartLine <= procedure.EndLine
                && (scope < 0 || procedure.DeclLine >= page.Procedures[scope].DeclLine))
                scope = p;
        }
        return scope;
    }

    private static int? ShellObjectLine(IReadOnlyList<VbToken> tokens, bool includeFileSystemObject)
    {
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (!IsIdentifier(tokens[i], "CreateObject") || tokens[i + 1].Kind != VbTokenKind.OpenParen
                || tokens[i + 2].Kind != VbTokenKind.String)
                continue;
            if (i > 0 && tokens[i - 1].Kind == VbTokenKind.Dot && (i < 2 || !IsIdentifier(tokens[i - 2], "Server")))
                continue;
            var progId = Unescape(tokens[i + 2].Text).Trim();
            if (ShellProgIds.Contains(progId)
                || (includeFileSystemObject && progId.Equals(FileSystemObject, StringComparison.OrdinalIgnoreCase)))
                return tokens[i].Line;
        }
        return null;
    }

    private static int? DynamicCodeLine(IReadOnlyList<VbToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == VbTokenKind.Identifier && DynamicCodeNames.Contains(tokens[i].Text)
                && (i == 0 || tokens[i - 1].Kind != VbTokenKind.Dot))
                return tokens[i].Line;
        }
        return null;
    }

    private static int? OnErrorResumeNextLine(IReadOnlyList<VbToken> tokens)
    {
        for (var i = 0; i + 3 < tokens.Count; i++)
        {
            if (IsWord(tokens[i], "On") && IsWord(tokens[i + 1], "Error") && IsWord(tokens[i + 2], "Resume")
                && IsWord(tokens[i + 3], "Next"))
                return tokens[i].Line;
        }
        return null;
    }

    /// <summary>Adds variables assigned an unwrapped Request value directly (<c>x = Request("a")</c>).</summary>
    private static void CollectTaintedAssignments(IReadOnlyList<VbToken> tokens, HashSet<string> tainted)
    {
        var analyzer = new ConcatAnalyzer(tokens, new HashSet<string>(), new HashSet<string>());
        for (var p = 0; p + 2 < tokens.Count; p++)
        {
            if (tokens[p].Kind != VbTokenKind.Identifier || !IsOperator(tokens[p + 1], "="))
                continue;
            if (p > 0 && !IsWord(tokens[p - 1], "Then") && !IsWord(tokens[p - 1], "Else") && !IsOperator(tokens[p - 1], ":"))
                continue;
            var end = analyzer.TermEnd(p + 2, tokens.Count);
            if (end > p + 2 && analyzer.IsRequestRef(p + 2, end)
                && (end == tokens.Count || IsOperator(tokens[end], ":") || IsWord(tokens[end], "Else")))
                tainted.Add(tokens[p].Text);
        }
    }

    private sealed class ConcatAnalyzer(IReadOnlyList<VbToken> tokens, HashSet<string> wrappers, HashSet<string> tainted)
    {
        /// <summary>Line of the first tainted operand of a SQL concatenation in the statement, or null.</summary>
        public int? FindSqlConcat()
        {
            if (FindInRange(0, tokens.Count) is { } line)
                return line;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == VbTokenKind.OpenParen && FindInRange(i + 1, Close(i, tokens.Count)) is { } inner)
                    return inner;
            }
            return null;
        }

        private int? FindInRange(int start, int end)
        {
            var i = start;
            while (i < end)
            {
                var termEnd = TermEnd(i, end);
                if (termEnd == i)
                {
                    i++;
                    continue;
                }
                var terms = new List<(int Start, int End)> { (i, termEnd) };
                while (termEnd < end && IsOperator(tokens[termEnd], "&"))
                {
                    var next = TermEnd(termEnd + 1, end);
                    if (next == termEnd + 1)
                        break;
                    terms.Add((termEnd + 1, next));
                    termEnd = next;
                }
                if (terms.Count >= 2 && terms.Any(t => IsSqlString(t.Start, t.End)))
                {
                    var bad = terms.FindIndex(t => Tainted(t.Start, t.End));
                    if (bad >= 0)
                        return tokens[terms[bad].Start].Line;
                }
                i = termEnd;
            }
            return null;
        }

        private bool IsSqlString(int start, int end) =>
            end == start + 1 && tokens[start].Kind == VbTokenKind.String && SqlWord.IsMatch(Unescape(tokens[start].Text));

        private bool Tainted(int start, int end)
        {
            if (IsAllowedWrapper(start, end))
                return false;
            if (IsRequestRef(start, end))
                return true;
            if (end == start + 1 && tokens[start].Kind == VbTokenKind.Identifier && tainted.Contains(tokens[start].Text))
                return true;
            for (var i = start; i < end; i++)
            {
                if (tokens[i].Kind != VbTokenKind.OpenParen)
                    continue;
                var close = Close(i, end);
                if (ContainsTaint(i + 1, close))
                    return true;
                i = close;
            }
            return false;
        }

        private bool ContainsTaint(int start, int end)
        {
            var i = start;
            while (i < end)
            {
                var termEnd = TermEnd(i, end);
                if (termEnd == i)
                {
                    i++;
                    continue;
                }
                if (Tainted(i, termEnd))
                    return true;
                i = termEnd;
            }
            return false;
        }

        /// <summary><c>Request</c>, <c>Request.Form</c>, <c>Request.QueryString</c> or <c>Request.Cookies</c>, with or without arguments.</summary>
        public bool IsRequestRef(int start, int end)
        {
            if (!IsIdentifier(tokens[start], "Request") || (start > 0 && tokens[start - 1].Kind == VbTokenKind.Dot))
                return false;
            var j = start + 1;
            if (j + 1 < end && tokens[j].Kind == VbTokenKind.Dot && RequestCollections.Contains(tokens[j + 1].Text))
                j += 2;
            return j == end || (tokens[j].Kind == VbTokenKind.OpenParen && Close(j, end) == end - 1);
        }

        /// <summary>A whole-term call of CLng/CInt/… or a site sanitizer, or <c>Replace(x, "'", "''")</c>.</summary>
        private bool IsAllowedWrapper(int start, int end)
        {
            if (start + 1 >= end || tokens[start].Kind != VbTokenKind.Identifier
                || tokens[start + 1].Kind != VbTokenKind.OpenParen || Close(start + 1, end) != end - 1)
                return false;
            var name = tokens[start].Text;
            if (wrappers.Contains(name))
                return true;
            if (!name.Equals("Replace", StringComparison.OrdinalIgnoreCase))
                return false;
            var args = Arguments(start + 2, end - 1);
            return args.Count == 3 && IsStringLiteral(args[1], "'") && IsStringLiteral(args[2], "''");
        }

        private bool IsStringLiteral((int Start, int End) arg, string value) =>
            arg.End == arg.Start + 1 && tokens[arg.Start].Kind == VbTokenKind.String
            && Unescape(tokens[arg.Start].Text) == value;

        private List<(int Start, int End)> Arguments(int start, int end)
        {
            var args = new List<(int, int)>();
            var argStart = start;
            for (var i = start; i < end; i++)
            {
                if (tokens[i].Kind == VbTokenKind.OpenParen)
                    i = Close(i, end);
                else if (tokens[i].Kind == VbTokenKind.Comma)
                {
                    args.Add((argStart, i));
                    argStart = i + 1;
                }
            }
            args.Add((argStart, end));
            return args;
        }

        /// <summary>
        /// End (exclusive) of the operand starting at <paramref name="start"/>: a literal, a parenthesized group, or a
        /// name with member accesses and argument lists. Returns <paramref name="start"/> when no operand starts there.
        /// </summary>
        public int TermEnd(int start, int end)
        {
            if (start >= end)
                return start;
            var token = tokens[start];
            switch (token.Kind)
            {
                case VbTokenKind.String or VbTokenKind.Number or VbTokenKind.Date:
                    return start + 1;
                case VbTokenKind.OpenParen:
                    return Close(start, end) + 1;
                case VbTokenKind.Identifier:
                case VbTokenKind.Dot when start + 1 < end && tokens[start + 1].Kind is VbTokenKind.Identifier or VbTokenKind.Keyword:
                    break;
                default:
                    return start;
            }
            var j = token.Kind == VbTokenKind.Dot ? start + 2 : start + 1;
            while (j < end)
            {
                if (tokens[j].Kind == VbTokenKind.OpenParen)
                    j = Close(j, end) + 1;
                else if (tokens[j].Kind == VbTokenKind.Dot && j + 1 < end
                    && tokens[j + 1].Kind is VbTokenKind.Identifier or VbTokenKind.Keyword)
                    j += 2;
                else
                    break;
            }
            return Math.Min(j, end);
        }

        /// <summary>Index of the parenthesis closing the one at <paramref name="open"/>, or end - 1 when unbalanced.</summary>
        private int Close(int open, int end)
        {
            var depth = 0;
            for (var i = open; i < end; i++)
            {
                if (tokens[i].Kind == VbTokenKind.OpenParen)
                    depth++;
                else if (tokens[i].Kind == VbTokenKind.CloseParen && --depth == 0)
                    return i;
            }
            return end - 1;
        }
    }

    private static string Unescape(string literal)
    {
        var inner = literal.Length >= 2 && literal[^1] == '"' ? literal[1..^1] : literal.TrimStart('"');
        return inner.Replace("\"\"", "\"", StringComparison.Ordinal);
    }

    private static bool IsIdentifier(VbToken token, string text) =>
        token.Kind == VbTokenKind.Identifier && token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private static bool IsWord(VbToken token, string text) =>
        token.Kind is VbTokenKind.Identifier or VbTokenKind.Keyword && token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private static bool IsOperator(VbToken token, string text) =>
        token.Kind == VbTokenKind.Operator && token.Text == text;
}
