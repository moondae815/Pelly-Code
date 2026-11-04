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
/// argument list that differs from the procedure's input parameters is a warning. Without a development connection,
/// or when the server is unreachable, the check is recorded as skipped.
/// </summary>
public sealed class AspSpStep(ISqlExecutor? sql, FeedbackBuilder feedback) : IVerifierStep
{
    /// <summary>Diagnostic code of one missing procedure.</summary>
    public const string NotFoundCode = "AspSpNotFound";

    public string Name => "AspSp";

    public async Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Asp ?? throw new InvalidOperationException("ASP verification needs the ASP run state.");
        if (sql is null)
        {
            state.Record(Name, AspCheckStatus.Skipped, "개발 DB 연결(sql.connectionString)이 설정되지 않았습니다");
            return StepResult.Pass;
        }

        var calls = AspSpCallFinder.Find(AspPage.Parse(ctx.Current.Text), ctx.Options.Asp.SpCallFunctions);
        var original = state.OriginalSpCalls;
        // New names are checked for existence; a call whose (name, argument count) is new is checked for its arguments.
        var newNames = Distinct(calls.Where(c => !original.Any(o => o.Name.SameAs(c.Name))).Select(c => c.Name));
        var newCounted = calls
            .Where(c => c.ArgumentCount is not null && !original.Any(o => o.Name.SameAs(c.Name) && o.ArgumentCount == c.ArgumentCount))
            .ToList();

        try
        {
            if (newNames.Count > 0)
            {
                var missing = await sql.FindMissingAsync(newNames, ct);
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

            var warnings = await ArgumentWarningsAsync(newCounted, ct);
            if (warnings.Count > 0)
                state.Record(Name, AspCheckStatus.Warned, string.Join("; ", warnings));
            else
                state.Record(Name, AspCheckStatus.Passed);
            return StepResult.Pass;
        }
        catch (Exception ex) when (ex is SqlUnavailableException or Microsoft.Data.SqlClient.SqlException)
        {
            state.Record(Name, AspCheckStatus.Skipped,
                $"SQL 서버를 사용할 수 없습니다: {SqlConnectionText.MaskInText(ex.Message, ctx.Options.Sql.ConnectionString)}");
            return StepResult.Pass;
        }
    }

    private async Task<List<string>> ArgumentWarningsAsync(IReadOnlyList<AspSpCall> calls, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (calls.Count == 0)
            return warnings;
        var signatures = await sql!.GetProcedureSignaturesAsync(Distinct(calls.Select(c => c.Name)), ct);
        foreach (var call in calls)
        {
            if (signatures.FirstOrDefault(s => s.Name.SameAs(call.Name)) is not { } signature)
                continue;
            var inputs = signature.Parameters.Count(p => !p.IsOutput);
            if (call.ArgumentCount != inputs)
                warnings.Add($"{call.Name.Display} 인수 {call.ArgumentCount}개, 입력 매개변수 {inputs}개");
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
