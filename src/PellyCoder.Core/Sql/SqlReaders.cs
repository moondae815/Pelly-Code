using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;

namespace PellyCoder.Core.Sql;

/// <param name="Problem">User-facing reason (Korean) the connection was refused; null with a null <paramref name="Reader"/> means "not configured".</param>
public sealed record SqlReaderOpen(ISqlDefinitionReader? Reader, SqlEndpoint? Endpoint, string? Problem);

/// <summary>Builds readers after the allow-list check. Creating a reader never connects.</summary>
public static class SqlReaders
{
    /// <summary>All-null result when <c>sql.production</c> has no connection string.</summary>
    public static SqlReaderOpen Production(SqlOptions o, Func<string, ISqlDefinitionReader> factory)
    {
        var cs = o.Production.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            return new(null, null, null);

        var endpoint = SqlConnectionText.Endpoint(cs);
        if (endpoint.Problem is null && endpoint.Database is null)
            return new(null, endpoint, "운영 연결 문자열에 Database(Initial Catalog)가 없습니다");

        var check = SqlTargetPolicy.CheckProduction(endpoint, o.Production);
        return check.Allowed
            ? new(factory(cs), endpoint, null)
            : new(null, endpoint, check.Message ?? "운영 SQL 접속 대상이 허용되지 않습니다");
    }

    public static SqlReaderOpen Development(SqlOptions o, Func<string, ISqlDefinitionReader> factory)
    {
        var cs = o.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            return new(null, null, "SQL 연결 문자열이 없습니다. 비밀번호 없는 연결은 sql.connectionString, 비밀번호가 있는 연결은 환경 변수 PELLY_SQL_CONNECTION으로 지정하세요");

        var endpoint = SqlConnectionText.Endpoint(cs);
        if (endpoint.Problem is null && endpoint.Database is null)
            return new(null, endpoint, "연결 문자열에 Database(Initial Catalog)가 없습니다");

        var check = SqlTargetPolicy.Check(endpoint, o);
        return check.Allowed
            ? new(factory(cs), endpoint, null)
            : new(null, endpoint, check.Message ?? "SQL 접속 대상이 허용되지 않습니다");
    }
}
