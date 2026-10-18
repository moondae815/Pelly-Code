using Microsoft.SqlServer.TransactSql.ScriptDom;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Finds the one CREATE/ALTER object batch to edit: by <c>--object</c>, by <c>--line</c>, or the only one in the file
/// (several: selection UI, or exit 3 when not interactive — no model pick for SQL, R8). Refuses targets that cannot be
/// edited safely: parse errors, extra statements (R-T3) and forbidden statements already in the original (R2).
/// </summary>
public sealed class SqlLocatePhase(IUserInteraction ui, SqlOptions options) : IPhase
{
    public RunPhase Phase => RunPhase.Locate;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var request = ctx.Request;
        if (request.Line is not null && request.ObjectName is not null)
            return Env(ctx, "--line은 --object와 함께 쓸 수 없습니다");

        var script = SqlBatchSplitter.Split(ctx.Original.Text, options.CompatLevel);
        if (script.LexErrors.Count > 0)
            return Env(ctx, $"SQL 파일을 해석할 수 없습니다: {script.LexErrors[0]}");

        SqlBatch? target;
        if (request.ObjectName is { } objectText)
        {
            SqlObjectName wanted;
            try
            {
                wanted = SqlObjectName.Parse(objectText);
            }
            catch (FormatException ex)
            {
                return Env(ctx, $"--object 이름을 해석할 수 없습니다: {ex.Message}");
            }
            var matches = script.Batches.Where(b => b.ObjectName is { } n && n.SameAs(wanted)).ToList();
            if (matches.Count == 0)
                return NotFound(ctx, $"대상 객체 '{objectText}'을(를) 찾을 수 없습니다: {ctx.FilePath}");
            target = await ChooseAsync(ctx, matches, ct);
        }
        else if (request.Line is { } line)
        {
            target = script.Batches.FirstOrDefault(b => b.ObjectKind is not null && b.StartLine <= line && line <= b.EndLine);
            if (target is null)
                return NotFound(ctx, $"{line}번 줄은 프로시저·함수·뷰 정의 안이 아닙니다. 정의 안에 커서를 두고 실행하세요");
        }
        else
        {
            var all = script.ObjectBatches;
            if (all.Count == 0)
            {
                var broken = script.Batches.Where(b => b.ParseErrors.Count > 0).ToList();
                var hint = broken.Count == 0
                    ? ""
                    : $" (구문 오류가 있는 배치: {string.Join(", ", broken.Select(b => $"줄 {b.StartLine}-{b.EndLine}"))})";
                return NotFound(ctx, "파일에 프로시저·함수·뷰 정의가 없습니다" + hint);
            }
            var named = all.Where(b => b.ObjectName is not null).ToList();
            target = await ChooseAsync(ctx, named.Count > 0 ? named : all, ct);
        }
        if (target is null)
            return ctx.FailureKind == "TargetAmbiguous" ? PhaseOutcome.Ambiguous : PhaseOutcome.NotFound;

        if (target.ObjectName is not { } name || target.ParseErrors.Count > 0
            || CountStatements(target, options.CompatLevel, script.SettingsBefore(target.Index).QuotedIdentifier) != 1)
            return NotFound(ctx, $"대상 객체 배치에 정의 외의 문장이 있거나 구문 오류가 있어 편집할 수 없습니다 (줄 {target.StartLine}-{target.EndLine})");

        var forbidden = SqlSafetyGuard.ForbiddenIn(target, options.CompatLevel);
        if (forbidden.Count > 0)
        {
            ctx.Messages.Add($"대상 객체에 이미 금지 구문({string.Join(", ", forbidden)})이 있어 pelly로 편집할 수 없습니다 (PRD FR-07)");
            ctx.FailureKind = "SqlForbiddenInOriginal";
            return PhaseOutcome.SafetyRejected;
        }

        ctx.Target = new EditTarget(EditKind.Sql, ctx.FilePath, name.Display, new TextSpanInfo(target.StartLine, target.EndLine), null);
        ctx.ProjectStyle = "Sql";
        ctx.Sql = new SqlRunState(script, target.Index, name, target.ObjectKind!.Value, target.StatementType ?? "",
            script.SettingsBefore(target.Index), options.CompatLevel);
        return PhaseOutcome.Ok;
    }

    /// <summary>Top-level statements of the batch text (a safe edit target has exactly one).</summary>
    private static int CountStatements(SqlBatch batch, int compatLevel, bool quotedIdentifier)
    {
        var parser = SqlParsers.Create(compatLevel, quotedIdentifier);
        using var reader = new StringReader(batch.Text);
        var fragment = parser.Parse(reader, out _);
        return fragment is TSqlScript s ? s.Batches.Sum(b => b.Statements.Count) : 0;
    }

    /// <summary>One candidate is the target; several go to the selection UI, or fail as ambiguous when not interactive.</summary>
    private async Task<SqlBatch?> ChooseAsync(RunContext ctx, IReadOnlyList<SqlBatch> candidates, CancellationToken ct)
    {
        if (candidates.Count == 1)
            return candidates[0];
        if (!ui.IsInteractive)
        {
            ctx.Messages.Add($"객체가 {candidates.Count}개입니다. --object로 지정하세요: "
                + string.Join(", ", candidates.Select(Name)));
            ctx.FailureKind = "TargetAmbiguous";
            return null;
        }
        return await ui.SelectAsync("수정할 객체를 고르세요", candidates, b => $"{Name(b)} (줄 {b.StartLine}-{b.EndLine})", ct);
    }

    private static string Name(SqlBatch b) => b.ObjectName?.Display ?? $"줄 {b.StartLine}-{b.EndLine}";

    private static PhaseOutcome Env(RunContext ctx, string message)
    {
        ctx.Messages.Add(message);
        return PhaseOutcome.EnvError;
    }

    private static PhaseOutcome NotFound(RunContext ctx, string message)
    {
        ctx.Messages.Add(message);
        ctx.FailureKind = "TargetNotFound";
        return PhaseOutcome.NotFound;
    }
}
