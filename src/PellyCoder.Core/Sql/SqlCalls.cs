using Microsoft.Data.SqlClient;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Sql;

/// <summary>Shared connection execution and error mapping for every class that talks to SQL Server.</summary>
internal static class SqlCalls
{
    internal const int TimeoutErrorNumber = -2;

    /// <summary>
    /// Opens a pooled connection and runs <paramref name="body"/>. Connection-level failures become <see cref="SqlUnavailableException"/>;
    /// a cancelled running command (reported by SqlClient as a SqlException) becomes <see cref="OperationCanceledException"/>.
    /// </summary>
    internal static async Task<T> RunAsync<T>(string? connectionString, Func<SqlConnection, Task<T>> body, CancellationToken ct)
    {
        if (connectionString is null)
            throw new SqlUnavailableException("SQL 연결 문자열이 설정되지 않았습니다.");

        SqlConnection conn;
        try
        {
            conn = new SqlConnection(connectionString);
        }
        catch (ArgumentException)
        {
            throw new SqlUnavailableException($"SQL 연결 문자열을 해석할 수 없습니다: {SqlConnectionText.Mask(connectionString)}");
        }

        await using (conn)
        {
            try
            {
                await conn.OpenAsync(ct);
                return await body(conn);
            }
            catch (SqlException ex) when (ct.IsCancellationRequested)
            {
                // SqlClient reports a cancelled running command as SqlException ("Operation cancelled by user", Number 0)
                // with the connection still open; surface it as cancellation so the run ends with exit 2 (M3a T13, M2b M-1).
                throw new OperationCanceledException(ex.Message, ex, ct);
            }
            catch (SqlException ex) when (IsConnectionLevel(ex, conn))
            {
                throw new SqlUnavailableException(
                    $"SQL Server에 연결할 수 없습니다 ({SqlConnectionText.Mask(connectionString)}): {ex.Message.Replace(connectionString, SqlConnectionText.Mask(connectionString))}", ex);
            }
        }
    }

    internal static bool IsConnectionLevel(SqlException ex, SqlConnection conn)
        => conn.State != System.Data.ConnectionState.Open || ex.Number == TimeoutErrorNumber;
}
