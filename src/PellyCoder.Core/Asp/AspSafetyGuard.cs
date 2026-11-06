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
        var siteSanitizers = new HashSet<string>(sanitizers, StringComparer.OrdinalIgnoreCase);
        var permanent = SqlVariablePrePass(page, siteSanitizers);
        var result = new List<Keyed>();
        Walk(page, siteSanitizers, permanent, collect: false, (statement, sqlLine) =>
        {
            var key = string.Join(" ", statement.Tokens.Select(t => t.Text)).ToLowerInvariant();
            void Add(string rule, int line) =>
                result.Add(new Keyed(new AspSafetyFinding(rule, line, statement.Text.Trim()), key));

            if (ShellObjectLine(statement.Tokens, includeFileSystemObject) is { } shellLine)
                Add(ShellObject, shellLine);
            if (DynamicCodeLine(statement.Tokens) is { } dynamicLine)
                Add(DynamicCode, dynamicLine);
            if (OnErrorResumeNextLine(statement.Tokens) is { } errorLine)
                Add(OnErrorResumeNext, errorLine);
            if (sqlLine is { } line)
                Add(SqlConcatRequest, line);
        });
        return result;
    }

    /// <summary>
    /// Walks the statements in order with one <see cref="VariableState"/> per scope, starting from the scope's
    /// <paramref name="permanent"/> SQL variables; inside an If/Select/loop block assignments only add state. With
    /// <paramref name="collect"/>, each statement first adds its SQL-chain operands to the state (pre-pass).
    /// </summary>
    private static Dictionary<int, VariableState> Walk(
        AspPage page, HashSet<string> sanitizers, Dictionary<int, HashSet<string>> permanent, bool collect,
        Action<VbStatement, int?>? onStatement, Dictionary<int, HashSet<string>>? everSql = null)
    {
        var states = new Dictionary<int, VariableState>();
        var depths = new Dictionary<int, int>();
        foreach (var statement in page.Statements)
        {
            var scope = ScopeOf(page, statement);
            if (!states.TryGetValue(scope, out var state))
            {
                states[scope] = state = new VariableState();
                if (permanent.TryGetValue(scope, out var names))
                {
                    state.PermanentSql.UnionWith(names);
                    state.Sql.UnionWith(names);
                }
            }
            var depth = depths.GetValueOrDefault(scope);
            if (ClosesBlock(statement.Tokens))
                depth = Math.Max(0, depth - 1);
            var analyzer = new ConcatAnalyzer(statement.Tokens, sanitizers, state, assumeChainsReported: collect);
            if (collect)
                analyzer.CollectSqlVariables();
            var sqlLine = analyzer.Process(conditional: depth > 0);
            onStatement?.Invoke(statement, sqlLine);
            if (everSql is not null)
            {
                // A later reset (`w = ""` after the last SQL use) must not drop a variable that was SQL at any point.
                if (!everSql.TryGetValue(scope, out var ever))
                    everSql[scope] = ever = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ever.UnionWith(state.Sql);
            }
            if (OpensBlock(statement.Tokens))
                depth++;
            depths[scope] = depth;
        }
        return states;
    }

    /// <summary>
    /// Per scope, the variables that are SQL text from their first assignment (fixpoint over in-order walks): assigned
    /// an expression with a SQL-word literal or SQL variable, or used as an operand of a concatenation with SQL
    /// evidence. A WHERE accumulator (<c>w = " WHERE 1=1"</c> … <c>sql = "SELECT …" &amp; w</c>) is therefore SQL on
    /// every fragment line, so a tainted fragment is flagged on its own statement. A raw input value (tainted and not
    /// yet reported where it is used) is never SQL evidence and never becomes a SQL variable through a chain, so echoes
    /// of it in HTML or URLs are not SQL concatenations.
    /// </summary>
    private static Dictionary<int, HashSet<string>> SqlVariablePrePass(AspPage page, HashSet<string> sanitizers)
    {
        var permanent = new Dictionary<int, HashSet<string>>();
        bool changed;
        do
        {
            changed = false;
            var everSql = new Dictionary<int, HashSet<string>>();
            Walk(page, sanitizers, permanent, collect: true, onStatement: null, everSql);
            foreach (var (scope, ever) in everSql)
            {
                if (!permanent.TryGetValue(scope, out var names))
                    permanent[scope] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in ever)
                    changed |= names.Add(name);
            }
        }
        while (changed);
        return permanent;
    }

    /// <summary>Multi-line <c>If … Then</c>, <c>Select Case</c>, <c>For</c>, <c>Do</c> or <c>While</c>.</summary>
    private static bool OpensBlock(IReadOnlyList<VbToken> tokens) =>
        tokens.Count > 0
        && ((IsWord(tokens[0], "If") && IsWord(tokens[^1], "Then"))
            || (IsWord(tokens[0], "Select") && tokens.Count > 1 && IsWord(tokens[1], "Case"))
            || IsWord(tokens[0], "For") || IsWord(tokens[0], "Do") || IsWord(tokens[0], "While"));

    /// <summary><c>End If</c>, <c>End Select</c>, <c>Next</c>, <c>Loop</c> or <c>Wend</c>.</summary>
    private static bool ClosesBlock(IReadOnlyList<VbToken> tokens) =>
        tokens.Count > 0
        && ((IsWord(tokens[0], "End") && tokens.Count > 1 && (IsWord(tokens[1], "If") || IsWord(tokens[1], "Select")))
            || IsWord(tokens[0], "Next") || IsWord(tokens[0], "Loop") || IsWord(tokens[0], "Wend"));

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

    /// <summary>
    /// Per-scope variable states in statement order: tainted (holds a Request value not cleared by an allowed wrapper)
    /// and SQL (holds text built from a SQL-word literal). The last unconditional assignment before a use decides;
    /// assignments in a branch or loop body only add.
    /// </summary>
    private sealed class VariableState
    {
        public HashSet<string> Tainted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Sql { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>SQL variables from the pre-pass: never cleared by a non-SQL assignment.</summary>
        public HashSet<string> PermanentSql { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tainted variables whose every taint came from a statement already reported as SQL concatenation.</summary>
        public HashSet<string> TaintReported { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="assumeChainsReported">
    /// Pre-pass: taint assigned through a concatenation (<c>w = w &amp; " AND b='" &amp; Request("b")</c>) counts as
    /// reported, since that statement is reported once the target is known to be SQL; only a plain input read
    /// (<c>id = Request("id")</c>, <c>n = Trim(Request("n"))</c>) is a raw input value.
    /// </param>
    private sealed class ConcatAnalyzer(
        IReadOnlyList<VbToken> tokens, HashSet<string> sanitizers, VariableState state, bool assumeChainsReported = false)
    {
        /// <summary>
        /// Walks the statement's parts in order (split at top-level <c>:</c>, after <c>Then</c> and at <c>Else</c>):
        /// each part is checked against the state left by the earlier parts, then its assignment is applied. Parts
        /// after a single-line <c>Then</c>, and every part when <paramref name="conditional"/>, only add state.
        /// Returns the line of the first SQL concatenation hit, or null.
        /// </summary>
        public int? Process(bool conditional)
        {
            int? found = null;
            var afterThen = false;
            var partStart = 0;
            var depth = 0;
            for (var k = 0; k <= tokens.Count; k++)
            {
                var atEnd = k == tokens.Count;
                if (!atEnd)
                {
                    if (tokens[k].Kind == VbTokenKind.OpenParen)
                        depth++;
                    else if (tokens[k].Kind == VbTokenKind.CloseParen)
                        depth--;
                    if (depth > 0 || !(IsOperator(tokens[k], ":") || IsWord(tokens[k], "Then") || IsWord(tokens[k], "Else")))
                        continue;
                }
                var partEnd = !atEnd && IsWord(tokens[k], "Then") ? k + 1 : k;
                var hit = FindSqlConcat(partStart, partEnd);
                found ??= hit;
                ApplyAssignment(partStart, partEnd, conditional || afterThen,
                    hit is not null || (assumeChainsReported && AssignsConcatenation(partStart, partEnd)));
                if (!atEnd && IsWord(tokens[k], "Then"))
                    afterThen = true;
                partStart = k + 1;
            }
            return found;
        }

        /// <summary>
        /// <c>x = …</c> or <c>Set x = …</c>: x is tainted when the right side contains a tainted operand and a SQL
        /// variable when it contains a SQL-word literal or a SQL variable. A conditional assignment never clears.
        /// </summary>
        private void ApplyAssignment(int start, int end, bool conditional, bool reported)
        {
            var p = start < end && IsWord(tokens[start], "Set") ? start + 1 : start;
            if (p + 2 >= end || tokens[p].Kind != VbTokenKind.Identifier || !IsOperator(tokens[p + 1], "="))
                return;
            var name = tokens[p].Text;
            var tainted = ContainsTaint(p + 2, end);
            var sql = AssignsSql(p + 2, end);
            // Taint introduced by a reported statement is not reported again where the variable is used; taint from
            // an unreported assignment (e.g. `n = Request("n")`) still is.
            var wasUnreported = state.Tainted.Contains(name) && !state.TaintReported.Contains(name);
            if (!tainted)
                Set(state.TaintReported, name, false, conditional);
            else if (!reported || (conditional && wasUnreported))
                state.TaintReported.Remove(name);
            else
                state.TaintReported.Add(name);
            Set(state.Tainted, name, tainted, conditional);
            Set(state.Sql, name, sql, conditional || state.PermanentSql.Contains(name));
        }

        /// <summary>The part is an assignment whose right side joins terms with a top-level <c>&amp;</c> or <c>+</c>.</summary>
        private bool AssignsConcatenation(int start, int end)
        {
            var p = start < end && IsWord(tokens[start], "Set") ? start + 1 : start;
            if (p + 2 >= end || tokens[p].Kind != VbTokenKind.Identifier || !IsOperator(tokens[p + 1], "="))
                return false;
            var depth = 0;
            for (var k = p + 2; k < end; k++)
            {
                if (tokens[k].Kind == VbTokenKind.OpenParen)
                    depth++;
                else if (tokens[k].Kind == VbTokenKind.CloseParen)
                    depth--;
                else if (depth == 0 && (IsOperator(tokens[k], "&") || IsOperator(tokens[k], "+")))
                    return true;
            }
            return false;
        }

        private bool AssignsSql(int start, int end)
        {
            for (var k = start; k < end; k++)
            {
                if ((tokens[k].Kind == VbTokenKind.String && SqlWord.IsMatch(Unescape(tokens[k].Text)))
                    || (tokens[k].Kind == VbTokenKind.Identifier && tokens[k - 1].Kind != VbTokenKind.Dot
                        && state.Sql.Contains(tokens[k].Text) && !IsRawInput(tokens[k].Text)))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Pre-pass step: adds to the SQL set every assignment target whose right side holds SQL text and every
        /// plain-variable operand of a concatenation with a SQL literal or SQL variable. Returns whether the set grew.
        /// </summary>
        public bool CollectSqlVariables()
        {
            var before = state.Sql.Count;
            for (var start = 0; start < tokens.Count; start++)
            {
                // Every part start (statement start, after `:`/`Then`/`Else`) may begin an assignment.
                if (start > 0 && !(IsOperator(tokens[start - 1], ":") || IsWord(tokens[start - 1], "Then")
                    || IsWord(tokens[start - 1], "Else")))
                    continue;
                var q = start < tokens.Count && IsWord(tokens[start], "Set") ? start + 1 : start;
                if (q + 2 < tokens.Count && tokens[q].Kind == VbTokenKind.Identifier && IsOperator(tokens[q + 1], "=")
                    && AssignsSql(q + 2, PartEnd(q + 2)))
                    state.Sql.Add(tokens[q].Text);
            }
            CollectChainOperands(0, tokens.Count);
            return state.Sql.Count != before;
        }

        private int PartEnd(int start)
        {
            var depth = 0;
            for (var k = start; k < tokens.Count; k++)
            {
                if (tokens[k].Kind == VbTokenKind.OpenParen)
                    depth++;
                else if (tokens[k].Kind == VbTokenKind.CloseParen)
                    depth--;
                else if (depth <= 0 && (IsOperator(tokens[k], ":") || IsWord(tokens[k], "Else")))
                    return k;
            }
            return tokens.Count;
        }

        private void CollectChainOperands(int start, int end)
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
                var plus = false;
                while (termEnd < end && (IsOperator(tokens[termEnd], "&") || IsOperator(tokens[termEnd], "+")))
                {
                    plus |= IsOperator(tokens[termEnd], "+");
                    var next = TermEnd(termEnd + 1, end);
                    if (next == termEnd + 1)
                        break;
                    terms.Add((termEnd + 1, next));
                    termEnd = next;
                }
                if (IsSqlChain(terms, plus, null))
                {
                    // A raw input value joins the SQL set only through a SQL literal (it is then known to be SQL text
                    // for a later self-append), never through another variable, so echoes and URL chains do not spread.
                    var literal = terms.Any(t => IsSqlString(t.Start, t.End));
                    foreach (var t in terms)
                    {
                        if (t.End == t.Start + 1 && tokens[t.Start].Kind == VbTokenKind.Identifier
                            && (t.Start == 0 || tokens[t.Start - 1].Kind != VbTokenKind.Dot)
                            && (literal || !IsRawInput(tokens[t.Start].Text)))
                            state.Sql.Add(tokens[t.Start].Text);
                    }
                }
                foreach (var t in terms)
                {
                    for (var k = t.Start; k < t.End; k++)
                    {
                        if (tokens[k].Kind != VbTokenKind.OpenParen)
                            continue;
                        var close = Close(k, t.End);
                        CollectChainOperands(k + 1, close);
                        k = close;
                    }
                }
                i = termEnd;
            }
        }

        /// <summary>
        /// Two or more terms concatenated with SQL evidence among them: a SQL literal, a SQL variable that is not a raw
        /// input value (tainted and not reported; otherwise it would only be an echo of user input), or the
        /// <paramref name="selfAccumulator"/> term (a pre-pass SQL variable assigned back into itself, <c>w = w &amp; …</c>).
        /// </summary>
        private bool IsSqlChain(List<(int Start, int End)> terms, bool plus, string? selfAccumulator) =>
            terms.Count >= 2
            && (!plus || terms.Any(t => IsStringToken(t.Start, t.End) || IsSqlVariable(t.Start, t.End)))
            && terms.Any(t => IsSqlString(t.Start, t.End)
                || (IsSqlVariable(t.Start, t.End) && !IsRawInput(tokens[t.Start].Text))
                || IsSelf(t, selfAccumulator));

        private bool IsSelf((int Start, int End) term, string? selfAccumulator) =>
            selfAccumulator is not null && term.End == term.Start + 1 && tokens[term.Start].Kind == VbTokenKind.Identifier
            && tokens[term.Start].Text.Equals(selfAccumulator, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The target of an assignment part when it is a pre-pass SQL variable (used in a SQL-literal chain somewhere in
        /// the scope): appending to it builds SQL even while it holds a raw input value.
        /// </summary>
        private string? SqlAssignmentTarget(int start, int end)
        {
            var p = start < end && IsWord(tokens[start], "Set") ? start + 1 : start;
            return p + 2 < end && tokens[p].Kind == VbTokenKind.Identifier && IsOperator(tokens[p + 1], "=")
                && state.PermanentSql.Contains(tokens[p].Text)
                ? tokens[p].Text
                : null;
        }

        /// <summary>A variable holding user input whose taint was not reported where it came in.</summary>
        private bool IsRawInput(string name) => state.Tainted.Contains(name) && !state.TaintReported.Contains(name);

        private static void Set(HashSet<string> set, string name, bool value, bool conditional)
        {
            if (value)
                set.Add(name);
            else if (!conditional)
                set.Remove(name);
        }

        private int? FindSqlConcat(int start, int end)
        {
            if (FindInRange(start, end, SqlAssignmentTarget(start, end)) is { } line)
                return line;
            for (var i = start; i < end; i++)
            {
                if (tokens[i].Kind == VbTokenKind.OpenParen && FindInRange(i + 1, Close(i, end), null) is { } inner)
                    return inner;
            }
            return null;
        }

        private int? FindInRange(int start, int end, string? selfAccumulator)
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
                var plus = false;
                while (termEnd < end && (IsOperator(tokens[termEnd], "&") || IsOperator(tokens[termEnd], "+")))
                {
                    plus |= IsOperator(tokens[termEnd], "+");
                    var next = TermEnd(termEnd + 1, end);
                    if (next == termEnd + 1)
                        break;
                    terms.Add((termEnd + 1, next));
                    termEnd = next;
                }
                // `+` concatenates only when a string literal or SQL variable is involved; otherwise it is arithmetic.
                if (IsSqlChain(terms, plus, selfAccumulator))
                {
                    // A SQL variable whose taint was reported where it was introduced (the accumulator being
                    // extended, a WHERE fragment variable) is not a new taint source; nor is the self-appended
                    // accumulator, whose own taint counts where it is used in SQL.
                    var bad = terms.FindIndex(t => !IsReportedSqlVariable(t.Start, t.End) && !IsSelf(t, selfAccumulator)
                        && Tainted(t.Start, t.End));
                    if (bad >= 0)
                        return tokens[terms[bad].Start].Line;
                }
                i = termEnd;
            }
            return null;
        }

        private bool IsStringToken(int start, int end) =>
            end == start + 1 && tokens[start].Kind == VbTokenKind.String;

        private bool IsSqlVariable(int start, int end) =>
            end == start + 1 && tokens[start].Kind == VbTokenKind.Identifier && state.Sql.Contains(tokens[start].Text);

        private bool IsReportedSqlVariable(int start, int end) =>
            IsSqlVariable(start, end)
            && (!state.Tainted.Contains(tokens[start].Text) || state.TaintReported.Contains(tokens[start].Text));

        private bool IsSqlString(int start, int end) =>
            end == start + 1 && tokens[start].Kind == VbTokenKind.String && SqlWord.IsMatch(Unescape(tokens[start].Text));

        private bool Tainted(int start, int end)
        {
            if (IsAllowedWrapper(start, end))
                return false;
            if (IsRequestRef(start, end))
                return true;
            if (end == start + 1 && tokens[start].Kind == VbTokenKind.Identifier && state.Tainted.Contains(tokens[start].Text))
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
        private bool IsRequestRef(int start, int end)
        {
            if (!IsIdentifier(tokens[start], "Request") || (start > 0 && tokens[start - 1].Kind == VbTokenKind.Dot))
                return false;
            var j = start + 1;
            if (j + 1 < end && tokens[j].Kind == VbTokenKind.Dot && RequestCollections.Contains(tokens[j + 1].Text))
                j += 2;
            return j == end || (tokens[j].Kind == VbTokenKind.OpenParen && Close(j, end) == end - 1);
        }

        /// <summary>
        /// A whole-term call of CLng/CInt/… or <c>Replace(x, "'", "''")</c>, or of a site sanitizer, which may also be
        /// called through members (<c>Util.SafeSQL(x)</c>, <c>.SafeSQL(x)</c>) and matches on the last name.
        /// </summary>
        private bool IsAllowedWrapper(int start, int end)
        {
            var nameIndex = tokens[start].Kind == VbTokenKind.Dot ? start + 1 : start;
            if (nameIndex >= end || tokens[nameIndex].Kind != VbTokenKind.Identifier)
                return false;
            while (nameIndex + 2 < end && tokens[nameIndex + 1].Kind == VbTokenKind.Dot
                && tokens[nameIndex + 2].Kind is VbTokenKind.Identifier or VbTokenKind.Keyword)
                nameIndex += 2;
            if (nameIndex + 1 >= end || tokens[nameIndex + 1].Kind != VbTokenKind.OpenParen
                || Close(nameIndex + 1, end) != end - 1)
                return false;
            var name = tokens[nameIndex].Text;
            if (sanitizers.Contains(name))
                return true;
            if (nameIndex != start)
                return false;
            if (DefaultWrappers.Contains(name, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!name.Equals("Replace", StringComparison.OrdinalIgnoreCase))
                return false;
            var args = Arguments(nameIndex + 2, end - 1);
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
        private int TermEnd(int start, int end)
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
