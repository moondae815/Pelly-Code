using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// The shared PREFLIGHT, then the SQL checks: connection string, allow list, server reachable, VIEW DEFINITION.
/// All of it runs before any model generation request (the model server status check is not one).
/// </summary>
public sealed class SqlPreflightPhase(PreflightPhase inner, ISqlExecutor sql, SqlOptions options) : IPhase
{
    public RunPhase Phase => RunPhase.Preflight;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var outcome = await inner.ExecuteAsync(ctx, ct);
        if (outcome != PhaseOutcome.Ok)
            return outcome;

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            return Env(ctx, "SQL 연결 문자열이 없습니다. 비밀번호 없는 연결은 sql.connectionString, "
                + "비밀번호가 있는 연결은 환경 변수 PELLY_SQL_CONNECTION으로 지정하세요");

        var endpoint = SqlConnectionText.Endpoint(options.ConnectionString);
        if (endpoint.Database is null)
            return Env(ctx, "연결 문자열에 Database(Initial Catalog)가 없습니다");

        var check = SqlTargetPolicy.Check(endpoint, options);
        if (!check.Allowed)
        {
            ctx.Messages.Add(check.Message ?? "SQL 접속 대상이 허용되지 않습니다");
            ctx.FailureKind = "SqlTargetNotAllowed";
            return PhaseOutcome.SafetyRejected;
        }

        SqlServerProbe probe;
        try
        {
            probe = await sql.ProbeAsync(ct);
        }
        catch (SqlUnavailableException ex)
        {
            return Env(ctx, $"SQL 서버에 연결할 수 없습니다 ({endpoint.DataSource}/{endpoint.Database}): {ex.Message}. pelly doctor로 확인하세요");
        }

        if (!probe.CanViewDefinition)
        {
            ctx.FailureKind = "SqlPermission";
            return Env(ctx, "검증 계정에 VIEW DEFINITION 권한이 없어 객체 존재 검사를 할 수 없습니다. "
                + "DBA에게 GRANT VIEW DEFINITION을 요청하세요 (설계 §8.10)");
        }

        return PhaseOutcome.Ok;
    }

    private static PhaseOutcome Env(RunContext ctx, string message)
    {
        ctx.Messages.Add(message);
        return PhaseOutcome.EnvError;
    }
}
