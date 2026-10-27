using System.IO.Abstractions;
using System.Text.Json;

namespace PellyCoder.Core.Impact;

/// <summary>
/// The full list behind the SQL impact warnings (design §7.3 ③-6): the seeds, every table a seed writes with its written
/// columns and counts, and every candidate module with its overlapping columns — the warnings show at most ten per table.
/// </summary>
public static class SqlImpactList
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Writes (overwrites) <paramref name="path"/>, creating its folder. IO failures are the caller's to handle.</summary>
    public static void Write(IFileSystem fs, string path, string? specId, IReadOnlyList<SqlImpactSeed> seeds, SqlImpactResult result)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(result);

        var list = new
        {
            specId,
            basis = result.Basis switch { SqlImpactBasis.Prod => "prod", SqlImpactBasis.Dev => "dev", _ => null },
            unavailable = result.Unavailable,
            seeds = seeds.Select(s => new { taskId = s.TaskId, line = s.Line, @object = SqlImpactFinder.Display(s.Object) }),
            newObjectSeeds = result.NewObjectSeeds,
            tables = result.Tables.Select(t =>
            {
                var table = SqlImpactFinder.Display(t.Table);
                var rows = result.FullLists.GetValueOrDefault($"{t.SeedTaskId}|{table}") ?? [];
                return new
                {
                    seedTaskId = t.SeedTaskId,
                    table,
                    writtenColumns = t.WrittenColumns,
                    modules = t.Modules,
                    warned = t.Warned,
                    omitted = t.Omitted,
                    unverifiable = t.Unverifiable,
                    rows = rows.Select(r => new
                    {
                        module = SqlImpactFinder.Display(r.Module),
                        overlapColumns = r.OverlapColumns,
                        unverifiable = r.Unverifiable,
                    }),
                };
            }),
            targets = result.Targets.Select(t => new
            {
                seedTaskId = t.SeedTaskId,
                kind = t.TargetKind.ToString(),
                path = t.Path,
                member = t.Member,
                reason = t.Reason,
            }),
        };

        var dir = fs.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) fs.Directory.CreateDirectory(dir);
        fs.File.WriteAllText(path, JsonSerializer.Serialize(list, Json));
    }
}
