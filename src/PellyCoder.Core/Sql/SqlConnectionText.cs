using PellyCoder.Core.Config;
using System.Data.Common;

namespace PellyCoder.Core.Sql;

public sealed record SqlEndpoint(string DataSource, string Host, string? Database);

public sealed record SqlTargetCheck(bool Allowed, string? Message);

/// <summary>Connection-string helpers that avoid a SqlClient dependency in Core (BCL parser only).</summary>
public static class SqlConnectionText
{
    private static readonly string[] PasswordKeys = ["Password", "Pwd"];
    private static readonly string[] ServerKeys = ["Server", "Data Source", "Address", "Addr", "Network Address"];
    private static readonly string[] DatabaseKeys = ["Database", "Initial Catalog"];

    public static bool HasPassword(string cs)
    {
        var b = Parse(cs);
        return b is not null && PasswordKeys.Any(b.ContainsKey);
    }

    /// <summary>Replaces password values with ***; unparsable text is masked entirely.</summary>
    public static string Mask(string cs)
    {
        var b = Parse(cs);
        if (b is null) return "***";
        foreach (var key in PasswordKeys)
            if (b.ContainsKey(key)) b[key] = "***";
        return b.ConnectionString;
    }

    public static SqlEndpoint Endpoint(string cs)
    {
        var b = Parse(cs);
        var dataSource = (b is null ? null : First(b, ServerKeys))?.Trim() ?? "";
        var database = b is null ? null : First(b, DatabaseKeys)?.Trim();
        if (string.IsNullOrEmpty(database)) database = null;

        var host = dataSource;
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:" })
            if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { host = host[prefix.Length..]; break; }
        var comma = host.IndexOf(',');
        if (comma >= 0) host = host[..comma];
        var slash = host.IndexOf('\\');
        var hostOnly = slash >= 0 ? host[..slash] : host;
        // DataSource keeps the instance but drops the protocol prefix and port.
        return new SqlEndpoint(host, hostOnly, database);
    }

    private static DbConnectionStringBuilder? Parse(string cs)
    {
        try { return new DbConnectionStringBuilder { ConnectionString = cs }; }
        catch (ArgumentException) { return null; }
    }

    private static string? First(DbConnectionStringBuilder b, string[] keys)
    {
        foreach (var k in keys)
            if (b.TryGetValue(k, out var v) && v is not null) return v.ToString();
        return null;
    }
}

public static class SqlTargetPolicy
{
    public static SqlTargetCheck Check(SqlEndpoint endpoint, SqlOptions options)
    {
        var hostOk = options.AllowedHosts.Any(h =>
            string.Equals(h, endpoint.DataSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(h, endpoint.Host, StringComparison.OrdinalIgnoreCase));
        if (!hostOk)
            return new(false, $"SQL 서버 '{endpoint.DataSource}'이(가) sql.allowedHosts에 없습니다 — 접속을 허용하려면 sql.allowedHosts에 추가하세요");

        var dbOk = endpoint.Database is not null
            && options.AllowedDatabases.Any(d => string.Equals(d, endpoint.Database, StringComparison.OrdinalIgnoreCase));
        if (!dbOk)
            return new(false, $"데이터베이스 '{endpoint.Database}'이(가) sql.allowedDatabases에 없습니다 — 접속을 허용하려면 sql.allowedDatabases에 추가하세요");

        return new(true, null);
    }
}
