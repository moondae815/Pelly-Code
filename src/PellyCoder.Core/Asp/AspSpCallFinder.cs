using System.Text.RegularExpressions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Asp;

/// <summary>A stored procedure call found in ASP code. <see cref="ArgumentCount"/> is null when it cannot be counted.</summary>
public sealed record AspSpCall(SqlObjectName Name, int Line, int? ArgumentCount);

/// <summary>Recognises a few common ADO call shapes at string level (design 4.6).</summary>
public static class AspSpCallFinder
{
    private const string NamePart = @"(?:\[[^\]]+\]|[\w#$]+)";
    private const string NamePattern = NamePart + @"(?:\." + NamePart + @")*";

    private static readonly Regex ExecPattern = new(
        @"^\s*(?:exec|execute)\s+(?:@\w+\s*=\s*)?(?<name>" + NamePattern + @")(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex CallPattern = new(
        @"\{\s*(?:\?\s*=\s*)?call\s+(?<name>" + NamePattern + @")\s*(?<rest>\(.*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex FirstWordPattern = new(
        @"^\s*(?<name>" + NamePattern + @")(?<rest>\s.*)?$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly HashSet<string> HttpVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "DELETE", "HEAD", "PATCH", "OPTIONS",
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "EXEC", "EXECUTE", "MERGE", "WITH", "CREATE", "DROP", "ALTER", "TRUNCATE",
        "DECLARE", "SET", "IF", "BEGIN", "USE", "FROM",
    };

    public static IReadOnlyList<AspSpCall> Find(
        AspPage page, IReadOnlyList<AspSpCallFunction> wrappers, TextSpanInfo? within = null)
    {
        var calls = new List<AspSpCall>();
        foreach (var statement in page.Statements)
        {
            var scope = ScopeOf(page, statement);
            var found = new List<AspSpCall>();
            FindInStatement(page, statement, scope, wrappers, found);
            foreach (var call in found)
            {
                if (within is { } span && (call.Line < span.StartLine || call.Line > span.EndLine))
                    continue;
                if (!calls.Any(c => c.Line == call.Line && c.Name == call.Name))
                    calls.Add(call);
            }
        }
        return calls;
    }

    private sealed record Scope(int StartLine, int EndLine);

    private static Scope ScopeOf(AspPage page, VbStatement statement)
    {
        var procedure = page.Procedures.FirstOrDefault(p => statement.StartLine >= p.StartLine && statement.StartLine <= p.EndLine);
        return procedure is null ? new Scope(1, int.MaxValue) : new Scope(procedure.StartLine, procedure.EndLine);
    }

    private static IEnumerable<VbStatement> InScope(AspPage page, Scope scope) =>
        page.Statements.Where(s => s.StartLine >= scope.StartLine && s.StartLine <= scope.EndLine);

    private static void FindInStatement(
        AspPage page, VbStatement statement, Scope scope, IReadOnlyList<AspSpCallFunction> wrappers, List<AspSpCall> found)
    {
        var tokens = statement.Tokens;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == VbTokenKind.String)
            {
                var previous = i > 0 ? tokens[i - 1] : null;
                var beforePrevious = i > 1 ? tokens[i - 2] : null;
                var concatenated = i + 1 < tokens.Count && tokens[i + 1].Kind == VbTokenKind.Operator && tokens[i + 1].Text == "&";
                var value = Unquote(token.Text);
                if (previous is { Kind: VbTokenKind.Operator, Text: "&" })
                    continue; // a later piece of a concatenation is not the start of a SQL text

                if (previous is { Kind: VbTokenKind.Operator, Text: "=" } && beforePrevious is { Kind: VbTokenKind.Identifier }
                    && beforePrevious.Text.Equals("CommandText", StringComparison.OrdinalIgnoreCase)
                    && i > 2 && tokens[i - 3].Kind == VbTokenKind.Dot)
                {
                    AddFromLiteral(value, token.Line, concatenated, commandText: true, page, scope, found);
                }
                else if (previous is { Kind: VbTokenKind.Identifier } && previous.Text.Equals("Open", StringComparison.OrdinalIgnoreCase)
                    && beforePrevious is { Kind: VbTokenKind.Dot })
                {
                    var hasMoreArguments = i + 1 < tokens.Count && tokens[i + 1].Kind == VbTokenKind.Comma;
                    var receiver = i > 2 && tokens[i - 3].Kind == VbTokenKind.Identifier ? tokens[i - 3].Text : null;
                    if (receiver is not null && IsNonRecordset(page, scope, receiver))
                        continue;
                    AddFromOpen(value, token.Line, concatenated, hasMoreArguments, page, scope, found);
                }
                else if (LooksLikeSqlContext(statement))
                {
                    AddFromLiteral(value, token.Line, concatenated, commandText: false, page, scope, found);
                }
            }
            else if (token.Kind == VbTokenKind.Identifier)
            {
                var wrapper = wrappers.FirstOrDefault(w => w.Name.Equals(token.Text, StringComparison.OrdinalIgnoreCase));
                if (wrapper is not null && !(i > 0 && tokens[i - 1].Kind == VbTokenKind.Dot))
                    AddFromWrapper(tokens, i, wrapper.Argument, found);
            }
        }
    }

    private static void AddFromLiteral(
        string value, int line, bool concatenated, bool commandText, AspPage page, Scope scope, List<AspSpCall> found)
    {
        if (ExecPattern.Match(value) is { Success: true } exec)
        {
            if (concatenated && exec.Groups["rest"].Value.Length == 0)
                return; // "usp_" & x: the name continues outside the literal
            Add(found, exec.Groups["name"].Value, line, concatenated ? null : CountCommaArguments(exec.Groups["rest"].Value));
            return;
        }
        if (CallPattern.Match(value) is { Success: true } call)
        {
            var rest = call.Groups["rest"];
            Add(found, call.Groups["name"].Value, line, rest.Success ? CountParenArguments(rest.Value) : (int?)null);
            return;
        }
        if (!commandText || concatenated)
            return;
        // CommandText = "usp_X": a procedure name when the same procedure/page sets a stored procedure command type,
        // or when the text is a single word.
        var trimmed = value.Trim();
        var match = FirstWordPattern.Match(trimmed);
        if (!match.Success || SqlKeywords.Contains(match.Groups["name"].Value))
            return;
        if (!HasWhitespace(trimmed) || HasStoredProcType(page, scope))
            Add(found, match.Groups["name"].Value, line, AppendCount(page, scope));
    }

    private static void AddFromOpen(
        string value, int line, bool concatenated, bool hasMoreArguments, AspPage page, Scope scope, List<AspSpCall> found)
    {
        if (value.Contains('=') || value.Contains(';'))
            return;
        if (ExecPattern.IsMatch(value) || CallPattern.IsMatch(value))
        {
            AddFromLiteral(value, line, concatenated, commandText: false, page, scope, found);
            return;
        }
        var match = FirstWordPattern.Match(value);
        if (!match.Success || SqlKeywords.Contains(match.Groups["name"].Value) || HttpVerbs.Contains(match.Groups["name"].Value))
            return;
        // "rs.Open "usp_X a, b", conn": a name followed by arguments or by further Open arguments, not a bare string.
        if (!match.Groups["rest"].Success && !hasMoreArguments)
            return;
        var rest = match.Groups["rest"].Success ? match.Groups["rest"].Value : "";
        var count = concatenated ? null : match.Groups["rest"].Success ? CountCommaArguments(rest) : AppendCount(page, scope);
        Add(found, match.Groups["name"].Value, line, count);
    }

    private static void AddFromWrapper(IReadOnlyList<VbToken> tokens, int nameIndex, int argument, List<AspSpCall> found)
    {
        var i = nameIndex + 1;
        if (i < tokens.Count && tokens[i].Kind == VbTokenKind.OpenParen)
            i++;
        var depth = 0;
        var current = 1;
        for (; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == VbTokenKind.OpenParen)
                depth++;
            else if (t.Kind == VbTokenKind.CloseParen)
            {
                if (depth == 0)
                    return;
                depth--;
            }
            else if (t.Kind == VbTokenKind.Comma && depth == 0)
            {
                current++;
                if (current > argument)
                    return;
            }
            else if (current == argument && depth == 0 && t.Kind == VbTokenKind.String)
            {
                if (i + 1 < tokens.Count && tokens[i + 1] is { Kind: VbTokenKind.Operator, Text: "&" })
                    return; // "usp_" & x: the name is not a plain literal
                if (i > 0 && tokens[i - 1] is { Kind: VbTokenKind.Operator, Text: "&" })
                    return;
                Add(found, Unquote(t.Text).Trim(), t.Line, null);
                return;
            }
        }
    }

    private static void Add(List<AspSpCall> found, string name, int line, int? count)
    {
        try
        {
            found.Add(new AspSpCall(SqlObjectName.Parse(name), line, count));
        }
        catch (FormatException)
        {
        }
    }

    /// <summary>
    /// A bare "EXEC x" literal counts only when it is passed to a SQL call (Execute/Open/CommandText) or assigned to a
    /// variable; output statements such as Response.Write "Execute failed" do not.
    /// </summary>
    private static bool LooksLikeSqlContext(VbStatement statement)
    {
        var t = statement.Tokens;
        if (t.Count > 0 && t[0].Kind == VbTokenKind.Identifier && t[0].Text.Equals("Response", StringComparison.OrdinalIgnoreCase))
            return false;
        if (t.Any(x => x.Kind == VbTokenKind.Identifier
                && (x.Text.Equals("Execute", StringComparison.OrdinalIgnoreCase)
                    || x.Text.Equals("Open", StringComparison.OrdinalIgnoreCase)
                    || x.Text.Equals("CommandText", StringComparison.OrdinalIgnoreCase))))
            return true;
        return t.Count > 2 && t[0].Kind == VbTokenKind.Identifier && t[1] is { Kind: VbTokenKind.Operator, Text: "=" };
    }

    /// <summary>The receiver was assigned from CreateObject of an XMLHTTP or ADODB.Connection object in the same scope.</summary>
    private static bool IsNonRecordset(AspPage page, Scope scope, string receiver)
    {
        foreach (var statement in InScope(page, scope))
        {
            var t = statement.Tokens.Where(x => !(x.Kind == VbTokenKind.Keyword && x.Text.Equals("Set", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (t.Length > 2 && t[0].Kind == VbTokenKind.Identifier && t[0].Text.Equals(receiver, StringComparison.OrdinalIgnoreCase)
                && t[1] is { Kind: VbTokenKind.Operator, Text: "=" }
                && t.Any(x => x.Kind == VbTokenKind.Identifier && x.Text.Equals("CreateObject", StringComparison.OrdinalIgnoreCase))
                && t.Any(x => x.Kind == VbTokenKind.String
                    && (x.Text.Contains("XMLHTTP", StringComparison.OrdinalIgnoreCase)
                        || x.Text.Contains("ADODB.Connection", StringComparison.OrdinalIgnoreCase))))
                return true;
        }
        return false;
    }

    private static bool HasWhitespace(string text) => text.Any(char.IsWhiteSpace);

    private static string Unquote(string raw) =>
        raw.Length >= 2 && raw[0] == '"' ? raw[1..^1].Replace("\"\"", "\"") : raw;

    private static bool HasStoredProcType(AspPage page, Scope scope)
    {
        foreach (var statement in InScope(page, scope))
        {
            var t = statement.Tokens;
            for (var i = 0; i + 2 < t.Count; i++)
            {
                if (t[i].Kind == VbTokenKind.Identifier && t[i].Text.Equals("CommandType", StringComparison.OrdinalIgnoreCase)
                    && t[i + 1] is { Kind: VbTokenKind.Operator, Text: "=" }
                    && (t[i + 2] is { Kind: VbTokenKind.Number, Text: "4" }
                        || t[i + 2].Text.Equals("adCmdStoredProc", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
    }

    private static int? AppendCount(AspPage page, Scope scope)
    {
        var count = 0;
        foreach (var statement in InScope(page, scope))
        {
            var t = statement.Tokens;
            for (var i = 0; i + 2 < t.Count; i++)
            {
                if (t[i].Kind == VbTokenKind.Dot && t[i + 1].Text.Equals("Parameters", StringComparison.OrdinalIgnoreCase)
                    && t[i + 2].Kind == VbTokenKind.Dot && i + 3 < t.Count
                    && t[i + 3].Text.Equals("Append", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
        }
        return count > 0 ? count : null;
    }

    /// <summary>Top-level comma-separated arguments of <c>EXEC name a, b</c>; quotes and parentheses are skipped.</summary>
    private static int CountCommaArguments(string rest)
    {
        if (rest.Trim().Length == 0)
            return 0;
        var count = 1;
        var depth = 0;
        var inQuote = false;
        foreach (var c in rest)
        {
            if (c == '\'')
                inQuote = !inQuote;
            else if (inQuote)
                continue;
            else if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (c == ',' && depth == 0)
                count++;
        }
        return count;
    }

    /// <summary><c>{call x(?, ?)}</c>: arguments inside the parentheses.</summary>
    private static int? CountParenArguments(string rest)
    {
        var close = rest.LastIndexOf(')');
        if (close < 1)
            return null;
        return CountCommaArguments(rest[1..close]);
    }
}
