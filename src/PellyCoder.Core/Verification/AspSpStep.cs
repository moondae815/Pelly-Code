using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Stored procedures the edit started calling must exist in the development database (design §4.5.8); a countable
/// argument list that differs from the procedure's input parameters is a warning. A procedure an earlier sql task changed
/// (<see cref="RunRequest.ChangedProcedures"/>) is judged by its file signature instead: it exists even when the database
/// does not have it yet (A2 design §4.4). Without a development connection only those procedures are checked and the rest
/// are recorded as skipped; when the server is unreachable the database part is recorded as skipped.
/// </summary>
public sealed class AspSpStep(ISqlExecutor? sql, FeedbackBuilder feedback) : IVerifierStep
{
    /// <summary>Diagnostic code of one missing procedure.</summary>
    public const string NotFoundCode = "AspSpNotFound";

    private const string NoSqlDetail = "개발 DB 연결(sql.connectionString)이 설정되지 않았습니다";

    public string Name => "AspSp";

    public async Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Asp ?? throw new InvalidOperationException("ASP verification needs the ASP run state.");
        var changed = ctx.Request.ChangedProcedures;
        if (sql is null && changed is not { Count: > 0 })
        {
            state.Record(Name, AspCheckStatus.Skipped, NoSqlDetail);
            return StepResult.Pass;
        }

        var calls = AspSpCallFinder.Find(AspPage.Parse(ctx.Current.Text), ctx.Options.Asp.SpCallFunctions);
        var original = state.OriginalSpCalls;
        // New names are checked for existence; a call whose (name, argument count) is new is checked for its arguments.
        var newNames = Distinct(calls.Where(c => !original.Any(o => o.Name.SameAs(c.Name))).Select(c => c.Name));
        var newCounted = calls
            .Where(c => c.ArgumentCount is not null && !original.Any(o => o.Name.SameAs(c.Name) && o.ArgumentCount == c.ArgumentCount))
            .ToList();

        // A changed procedure exists (its sql task wrote it) and has its file's parameters: no database needed.
        bool InFile(SqlObjectName n) => ChangedProcedure.Find(changed, n) is not null;
        var fileWarnings = ArgumentWarnings(newCounted.Where(c => InFile(c.Name)),
            c => ChangedProcedure.Find(changed, c.Name)!.Signature, allowOmittedDefaults: true);
        var dbNames = newNames.Where(n => !InFile(n)).ToList();
        var dbCounted = newCounted.Where(c => !InFile(c.Name)).ToList();

        if (sql is null)
            return Done(state, fileWarnings, dbNames.Count > 0 || dbCounted.Count > 0 ? NoSqlDetail : null);

        try
        {
            if (dbNames.Count > 0)
            {
                var missing = await sql.FindMissingAsync(dbNames, ct);
                if (missing.Count > 0)
                {
                    var file = Path.GetFileName(ctx.FilePath);
                    var errors = calls.Where(c => missing.Any(m => m.SameAs(c.Name)))
                        .Select(c => new Diagnostic(file, c.Line, 0, "error", NotFoundCode, c.Name.Display)).ToList();
                    var names = missing.Select(m => m.Display).ToList();
                    state.Record(Name, AspCheckStatus.Failed, string.Join(", ", names));
                    return StepResult.Fail(feedback.ForAspSpNotFound(names), errors, [], FailureKinds.AspSpNotFound);
                }
            }

            var warnings = fileWarnings;
            if (dbCounted.Count > 0)
            {
                var signatures = await sql.GetProcedureSignaturesAsync(Distinct(dbCounted.Select(c => c.Name)), ct);
                warnings = [.. warnings.Concat(ArgumentWarnings(dbCounted, c => signatures.FirstOrDefault(s => s.Name.SameAs(c.Name)))).Distinct()];
            }
            return Done(state, warnings, null);
        }
        catch (Exception ex) when (ex is SqlUnavailableException or Microsoft.Data.SqlClient.SqlException)
        {
            return Done(state, fileWarnings,
                $"SQL 서버를 사용할 수 없습니다: {SqlConnectionText.MaskInText(ex.Message, ctx.Options.Sql.ConnectionString)}");
        }
    }

    /// <summary>
    /// Records the check: warned with <paramref name="warnings"/> (and <paramref name="skipped"/>, why the rest was not checked,
    /// after them), else skipped for <paramref name="skipped"/>, else passed. The step itself always passes here.
    /// </summary>
    private StepResult Done(AspRunState state, IReadOnlyList<string> warnings, string? skipped)
    {
        if (warnings.Count > 0)
            state.Record(Name, AspCheckStatus.Warned, string.Join("; ", skipped is null ? warnings : [.. warnings, skipped]));
        else if (skipped is not null)
            state.Record(Name, AspCheckStatus.Skipped, skipped);
        else
            state.Record(Name, AspCheckStatus.Passed);
        return StepResult.Pass;
    }

    private static List<string> ArgumentWarnings(IEnumerable<AspSpCall> calls, Func<AspSpCall, SqlProcedureSignature?> signatureOf,
        bool allowOmittedDefaults = false)
    {
        var warnings = new List<string>();
        foreach (var call in calls)
        {
            if (signatureOf(call) is not { } signature)
                continue;
            var inputs = signature.Parameters.Count(p => !p.IsOutput);
            // A signature read from a .sql file knows its defaults: trailing parameters with one may be left out of the call.
            var required = allowOmittedDefaults ? signature.Parameters.Count(p => !p.IsOutput && !p.HasDefault) : inputs;
            if (call.ArgumentCount < required || call.ArgumentCount > inputs)
                warnings.Add($"{call.Name.Display} 인수 {call.ArgumentCount}개, 입력 매개변수 {(required == inputs ? inputs.ToString() : $"{required}~{inputs}")}개");
        }
        return [.. warnings.Distinct()];
    }

    private static List<SqlObjectName> Distinct(IEnumerable<SqlObjectName> names)
    {
        var result = new List<SqlObjectName>();
        foreach (var name in names)
        {
            if (!result.Any(n => n.SameAs(name)))
                result.Add(name);
        }
        return result;
    }
}
