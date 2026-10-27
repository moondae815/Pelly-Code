using Microsoft.Data.SqlClient;

namespace PellyCoder.Integration.Tests.Infrastructure;

/// <summary>
/// The production-role corpus database: same server as <c>PELLY_TEST_SQL</c>, database <c>PellyCorpusProd</c>
/// (deployed by <c>eval/corpus/Sql/deploy.sh &lt;container&gt; &lt;password&gt; PellyCorpusProd</c>).
/// </summary>
public static class ProdDb
{
    public const string DatabaseName = "PellyCorpusProd";

    /// <summary>The test connection with the database switched to <see cref="DatabaseName"/>; null when PELLY_TEST_SQL is not set.</summary>
    public static string? ConnectionString => TestEnv.SqlConnection is { } cs ? With(cs) : null;

    private static string With(string cs) => new SqlConnectionStringBuilder(cs) { InitialCatalog = DatabaseName }.ConnectionString;
}
