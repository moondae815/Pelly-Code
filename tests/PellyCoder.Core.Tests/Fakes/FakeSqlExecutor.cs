using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Fakes;

/// <summary>Scriptable <see cref="ISqlExecutor"/> that records what it receives.</summary>
public sealed class FakeSqlExecutor : ISqlExecutor
{
    private Exception? _probeFailure;

    public SqlServerProbe Probe { get; set; } = new("Microsoft SQL Server (fake)", "FakeDb", true);
    public HashSet<SqlObjectName> Missing { get; } = [];
    public List<SqlTableSchema> Schemas { get; } = [];
    public Dictionary<string, IReadOnlyList<SqlObjectName>> Referenced { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? ReferencedFailure { get; set; }
    public Func<string, IReadOnlyList<SqlCompileError>> Compile { get; set; } = _ => [];

    public List<string> Compiled { get; } = [];
    public List<SqlSessionSettings> CompiledSettings { get; } = [];
    public List<string> Calls { get; } = [];

    public void ThrowOnProbe(Exception ex) => _probeFailure = ex;

    public Task<SqlServerProbe> ProbeAsync(CancellationToken ct)
    {
        Calls.Add("Probe");
        return _probeFailure is null ? Task.FromResult(Probe) : Task.FromException<SqlServerProbe>(_probeFailure);
    }

    public Task<IReadOnlyList<SqlObjectName>> FindMissingAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct)
    {
        Calls.Add("FindMissing");
        IReadOnlyList<SqlObjectName> result = names.Where(n => Missing.Any(m => m.SameAs(n))).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SqlTableSchema>> GetSchemasAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct)
    {
        Calls.Add("GetSchemas");
        IReadOnlyList<SqlTableSchema> result = Schemas.Where(s => names.Any(n => n.SameAs(s.Name))).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SqlObjectName>> GetReferencedEntitiesAsync(SqlObjectName obj, CancellationToken ct)
    {
        Calls.Add("GetReferencedEntities");
        if (ReferencedFailure is not null) return Task.FromException<IReadOnlyList<SqlObjectName>>(ReferencedFailure);
        return Task.FromResult(Referenced.TryGetValue(obj.Display, out var r) ? r : []);
    }

    public Task<IReadOnlyList<SqlCompileError>> CompileWithNoExecAsync(string batch, SqlSessionSettings settings, CancellationToken ct)
    {
        Calls.Add("Compile");
        Compiled.Add(batch);
        CompiledSettings.Add(settings);
        return Task.FromResult(Compile(batch));
    }
}
