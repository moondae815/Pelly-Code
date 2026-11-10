using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Re-reads the ASP page, re-finds the target (an earlier attempt may have shifted it) and builds the budgeted context
/// (design §4.4): the editable target, the page head, called procedure declarations, page variables, the signatures of the
/// stored procedures the target calls and, for a line range, the lines around it.
/// </summary>
public sealed class AspSlicePhase(FileGuard fileGuard, ITokenCounter tokens, BudgetOptions budget, ISqlExecutor? sql, AspOptions options,
    IFileSystem? fs = null, PellyOptions? pelly = null, ITextFileCodec? codec = null) : IPhase
{
    public const string SqlContextCheck = "SqlContext";
    private const int AroundLines = 10;
    public const string LockedSeparator = "... (locked include/directive line — not editable) ...";

    // Dropped from the front when the budget is short; the earlier, the lower the priority.
    private static readonly string[] DropOrder = ["around", "sql", "variables", "procedures", "page", "env"];

    private static readonly HashSet<string> VariableKinds = new(StringComparer.Ordinal) { "Dim", "ReDim", "Const", "Public", "Private" };

    public RunPhase Phase => RunPhase.Slice;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var current = fileGuard.Read(ctx.FilePath);
        if (current.Sha256 != ctx.Current.Sha256)
        {
            // Keep ctx.Current as the last content pelly knows, so rollback sees the change and does not overwrite it.
            ctx.Messages.Add($"pelly 실행 중에 파일이 다른 곳에서 바뀌었습니다. 더 진행하지 않습니다: {ctx.FilePath}");
            ctx.FailureKind = FailureKinds.FileChangedExternally;
            return PhaseOutcome.EnvError;
        }
        ctx.Current = current;

        var state = ctx.Asp!;
        if (!state.OriginalSpCallsSet)
            state.OriginalSpCalls = AspSpCallFinder.Find(state.OriginalPage, options.SpCallFunctions);

        var page = AspPage.Parse(current.Text);
        AspProcedure? procedure = null;
        if (state.Unit == AspTargetUnit.Procedure)
        {
            procedure = Refind(page, state);
            if (procedure is null)
                return NotFound(ctx);
            state.CurrentSpan = new TextSpanInfo(procedure.StartLine, procedure.EndLine);
        }
        var span = state.CurrentSpan;
        var lineCount = page.LineCount;
        if (span.StartLine < 1 || span.EndLine > lineCount || span.EndLine < span.StartLine)
            return NotFound(ctx);
        ctx.Target = ctx.Target! with { MemberSpan = span };

        var regions = state.EditableRegions(page);
        // Locked lines inside the span split it; the separator tells the model the chunks are not contiguous.
        var targetText = string.Join("\n" + LockedSeparator + "\n",
            regions.Select(r => string.Join('\n', Lines(page, r.Span.StartLine, r.Span.EndLine))));
        var targetPart = new SlicePart("target", targetText, tokens.Count(targetText), int.MaxValue);

        var candidates = new List<SlicePart>();
        void Add(string kind, string text, int priority)
        {
            if (text.Length > 0) candidates.Add(new SlicePart(kind, text, tokens.Count(text), priority));
        }
        Add("page", PageHead(page, state.EncodingName), 5);
        Add("procedures", Procedures(page, state, span, procedure), 4);
        Add("variables", Variables(page, state, span, procedure), 3);
        Add("sql", await SqlSignaturesAsync(ctx, page, state, span, ct), 2);
        // JS functions earlier apply tasks re-signed, when the target names them (W3 design §4.6).
        Add("js", ChangedJsFunction.LinesFor(ctx.Request.ChangedJsFunctions, targetText), 2);
        Add("env", ClientEnv(ctx, state), 6);
        if (state.Unit == AspTargetUnit.Lines)
            Add("around", Around(page, span, lineCount), 1);

        var hint = $"Classic ASP (VBScript); file encoding {state.EncodingName}; Option Explicit: {(page.OptionExplicit ? "yes" : "no")}";
        var tooLarge = targetPart.Tokens > budget.Context;
        var omitted = new List<SlicePart>();
        if (tooLarge)
        {
            omitted.AddRange(candidates);
            candidates.Clear();
        }
        else
        {
            foreach (var kind in DropOrder)
            {
                if (targetPart.Tokens + candidates.Sum(p => p.Tokens) <= budget.Context) break;
                var part = candidates.FirstOrDefault(p => p.Kind == kind);
                if (part is null) continue;
                candidates.Remove(part);
                omitted.Add(part);
            }
        }

        var included = new List<SlicePart> { targetPart };
        included.AddRange(candidates);
        ctx.SliceResult = new SliceResult(new Slice(included, omitted, hint), regions, tooLarge);
        if (tooLarge)
        {
            ctx.Messages.Add($"대상이 문맥 예산보다 큽니다 ({targetPart.Tokens} > budget.context {budget.Context} 토큰). 범위를 나누거나 예산을 늘리세요.");
            return PhaseOutcome.BudgetExceeded;
        }
        return PhaseOutcome.Ok;
    }

    /// <summary>
    /// The JS language level and jQuery version of the client blocks the edit overlaps (design 4.5); empty when none overlap.
    /// Built from the original page, so it stays the same across retries.
    /// </summary>
    private string ClientEnv(RunContext ctx, AspRunState state)
    {
        if (state.ClientEnv is { } cached) return cached;
        var page = state.OriginalPage;
        var text = ctx.Original.Text;
        var scripts = AspClientChecks.OverlappedBlocks(page, state.OriginalRegions())
            .Where(i => page.ClientBlocks[i].Kind == AspClientBlockKind.Script).ToList();
        if (scripts.Count == 0) return state.ClientEnv = "";

        var lines = new List<string>();
        foreach (var i in scripts)
        {
            var view = PageBlockView.Build(page, text, i);
            var plan = AspClientChecks.Plan(page, text, view, view, pelly?.Web.JsTarget);
            var level = JsLevels.EnvLine(plan.Level, plan.Reason is null ? null : "server code in the block or the block did not parse");
            var b = page.ClientBlocks[i];
            lines.Add(scripts.Count == 1 ? level : $"<script> block, lines {b.ContentStartLine}-{b.ContentEndLine}: {level}");
        }
        lines.Add(Jquery(ctx, state, page, text, scripts[0]));
        return state.ClientEnv = string.Join('\n', lines);
    }

    private string Jquery(RunContext ctx, AspRunState state, AspPage page, string text, int blockIndex)
    {
        if (fs is null || pelly is null || codec is null) return "jQuery: not found";
        try
        {
            var found = JqueryVersions.Find(fs, state.SiteRoot);
            var index = WebLocateCommon.SiteIndex(fs, ctx, pelly, state.SiteRoot, codec);
            var scripts = PageScripts.Collect(fs, state.SiteRoot, ctx.FilePath, page, blockIndex, codec, index.Value, found, pelly.Web.JqueryVersion, text);
            return scripts.Jquery is { } v ? $"jQuery {v} (source: {scripts.JquerySource})" : "jQuery: not found";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "jQuery: not found";
        }
    }

    /// <summary>The procedure with the original's name and class; among same-named ones, the one at the same position.</summary>
    private static AspProcedure? Refind(AspPage page, AspRunState state)
    {
        var original = state.Procedure!;
        bool Same(AspProcedure p) =>
            p.Name.Equals(original.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.ClassName, original.ClassName, StringComparison.OrdinalIgnoreCase);
        var before = state.OriginalPage.Procedures.Where(Same).ToList();
        var now = page.Procedures.Where(Same).ToList();
        var index = before.FindIndex(p => p.StartLine == original.StartLine);
        if (now.Count == 0 || now.Count != before.Count || index < 0)
            return now.Count == 1 ? now[0] : null;
        return now[index];
    }

    /// <summary>Directive, include and metadata lines verbatim, then Option Explicit and the encoding.</summary>
    private static string PageHead(AspPage page, string encodingName)
    {
        var lines = page.LockedLines.Order().Select(page.LineText).ToList();
        lines.Add($"Option Explicit: {(page.OptionExplicit ? "yes" : "no")}");
        lines.Add($"Encoding: {encodingName}");
        return string.Join('\n', lines);
    }

    /// <summary>Declaration lines (with the comments above) of the top-level procedures the target names, from the page and its includes.</summary>
    private static string Procedures(AspPage page, AspRunState state, TextSpanInfo span, AspProcedure? target)
    {
        var used = UsedNames(page, span);
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (target is not null) seen.Add(target.Name);

        void From(AspPage source, string? file, bool same)
        {
            foreach (var p in source.Procedures)
            {
                if (p.ClassName is not null || !used.Contains(p.Name)) continue;
                // A procedure defined inside the target range is already in the target.
                if (same && p.DeclLine >= span.StartLine && p.DeclLine <= span.EndLine) continue;
                if (!seen.Add(p.Name)) continue;
                if (file is not null) lines.Add($"' {file}");
                lines.AddRange(Lines(source, p.StartLine, p.DeclLine));
            }
        }

        From(page, null, same: true);
        foreach (var include in state.Includes.Files)
            From(include.Page, Path.GetFileName(include.Path.Replace('\\', '/')), same: false);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Module-level declaration lines and the first module-level assignment line of the page names the target uses
    /// (lines inside the target are not repeated). Names local to the target procedure are skipped; for a line range inside a
    /// procedure, that procedure's local declarations (Dim, Const, parameters) outside the range are shown instead.
    /// </summary>
    private static string Variables(AspPage page, AspRunState state, TextSpanInfo span, AspProcedure? target)
    {
        var enclosing = target ?? page.Procedures
            .Where(p => p.StartLine <= span.StartLine && span.EndLine <= p.EndLine)
            .OrderBy(p => p.EndLine - p.StartLine)
            .FirstOrDefault();
        var scope = enclosing is null ? null : enclosing.ClassName is null ? enclosing.Name : enclosing.ClassName + "." + enclosing.Name;
        var localDeclarations = scope is null ? [] : page.Declarations.Where(d => d.Procedure == scope).ToList();
        var used = UsedNames(page, span);
        if (enclosing is not null) used.Remove(enclosing.Name);

        var pageLines = new SortedSet<int>();
        bool Outside(int line) => line < span.StartLine || line > span.EndLine;
        foreach (var d in localDeclarations)
        {
            if (!used.Contains(d.Name)) continue;
            if (target is null && Outside(d.Line)) pageLines.Add(d.Line);
        }
        used.ExceptWith(localDeclarations.Select(d => d.Name));

        var lines = new List<string>();
        foreach (var name in used)
        {
            var declared = page.Declarations.FirstOrDefault(d => d.Procedure is null && VariableKinds.Contains(d.Kind)
                && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var assigned = page.NameUses.FirstOrDefault(u => u.Procedure is null && u.IsAssignment
                && u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (declared is not null && Outside(declared.Line)) pageLines.Add(declared.Line);
            if (assigned is not null && Outside(assigned.Line)) pageLines.Add(assigned.Line);
            if (declared is not null || assigned is not null) continue;

            foreach (var include in state.Includes.Files)
            {
                var d = include.Page.Declarations.FirstOrDefault(x => x.Procedure is null && VariableKinds.Contains(x.Kind)
                    && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (d is null) continue;
                lines.Add($"{include.Page.LineText(d.Line)} ' {Path.GetFileName(include.Path.Replace('\\', '/'))}");
                break;
            }
        }
        return string.Join('\n', pageLines.Select(page.LineText).Concat(lines.Distinct()));
    }

    /// <summary>
    /// Signatures of the stored procedures called inside the target range; empty when there are none or no SQL is available.
    /// A procedure an earlier sql task changed (<see cref="RunRequest.ChangedProcedures"/>) shows its file signature with a
    /// note instead of the development database's (A2 design §4.4).
    /// </summary>
    private async Task<string> SqlSignaturesAsync(RunContext ctx, AspPage page, AspRunState state, TextSpanInfo span, CancellationToken ct)
    {
        var changed = ctx.Request.ChangedProcedures;
        if (sql is null && changed is not { Count: > 0 })
        {
            state.Record(SqlContextCheck, AspCheckStatus.Skipped, NoSqlDetail);
            return "";
        }
        var names = new List<SqlObjectName>();
        foreach (var call in AspSpCallFinder.Find(page, options.SpCallFunctions, span))
        {
            if (!names.Any(n => n.SameAs(call.Name))) names.Add(call.Name);
        }
        if (names.Count == 0)
            return "";

        var fromDb = names.Where(n => ChangedProcedure.Find(changed, n) is null).ToList();
        IReadOnlyList<SqlProcedureSignature> signatures = [];
        if (fromDb.Count > 0 && sql is null)
            state.Record(SqlContextCheck, AspCheckStatus.Skipped, NoSqlDetail);
        else if (fromDb.Count > 0)
        {
            try
            {
                signatures = await sql!.GetProcedureSignaturesAsync(fromDb, ct);
            }
            catch (Exception ex) when (ex is SqlUnavailableException or Microsoft.Data.SqlClient.SqlException)
            {
                // The SQL context is optional: an unreachable server or a failed metadata query only skips it.
                state.Record(SqlContextCheck, AspCheckStatus.Skipped,
                    $"SQL 서버를 사용할 수 없습니다: {SqlConnectionText.MaskInText(ex.Message, ctx.Options.Sql.ConnectionString)}");
            }
        }

        var lines = new List<string>();
        foreach (var name in names)
        {
            var file = ChangedProcedure.Find(changed, name);
            if ((file?.Signature ?? signatures.FirstOrDefault(s => s.Name.SameAs(name))) is not { } s) continue;
            var parameters = s.Parameters.Select(p => $"{p.Name} {p.Type}{(p.IsOutput ? " OUTPUT" : "")}");
            var note = file is null ? "" : $" -- changed by {file.TaskId}, not deployed yet";
            lines.Add($"PROCEDURE {s.Name.Display} ({string.Join(", ", parameters)}){note}");
            if (s.FirstResultSet is { Count: > 0 } columns)
                lines.Add($"  RESULT ({string.Join(", ", columns.Select(c => $"{c.Name} {c.TypeDisplay}"))})");
        }
        return string.Join('\n', lines);
    }

    private const string NoSqlDetail = "개발 DB 연결(sql.connectionString)이 설정되지 않았습니다";

    /// <summary>Ten lines before and after a line range target, with a marker where the target sits.</summary>
    private static string Around(AspPage page, TextSpanInfo span, int lineCount)
    {
        var before = Lines(page, Math.Max(1, span.StartLine - AroundLines), span.StartLine - 1).ToList();
        var after = Lines(page, span.EndLine + 1, Math.Min(lineCount, span.EndLine + AroundLines)).ToList();
        if (before.Count == 0 && after.Count == 0)
            return "";
        return string.Join('\n', before.Append("... (target lines) ...").Concat(after));
    }

    /// <summary>Names used inside <paramref name="span"/>.</summary>
    private static HashSet<string> UsedNames(AspPage page, TextSpanInfo span) =>
        page.NameUses.Where(u => u.Line >= span.StartLine && u.Line <= span.EndLine)
            .Select(u => u.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Lines(AspPage page, int from, int to)
    {
        for (var line = from; line <= to; line++)
            yield return page.LineText(line);
    }

    private static PhaseOutcome NotFound(RunContext ctx)
    {
        ctx.Messages.Add($"수정 중인 대상 '{ctx.Target!.SymbolDisplay}'을(를) 다시 찾을 수 없습니다.");
        ctx.FailureKind = FailureKinds.TargetNotFound;
        return PhaseOutcome.NotFound;
    }
}
