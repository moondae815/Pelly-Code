using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Impact;

public class SqlImpactFinderTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";
    private static readonly Workspace Ws = new(Root, Path.Combine(Root, ".pelly"));
    private static readonly SqlObjectName SeedName = new("dbo", "usp_Seed");
    private static readonly SqlImpactSeed Seed = new("T1", 3, SeedName);
    private static readonly SqlObjectName Orders = new("dbo", "Orders");

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static MockFileSystem Fs(string? code = null)
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(Root);
        if (code is not null) fs.AddFile(P("Repo.cs"), new MockFileData(code));
        return fs;
    }

    private const string CallerCode = "class Repo { void Cancel() { var s = \"dbo.usp_Seed\"; } }";

    private static Task<SqlImpactResult> Run(FakeReader reader, MockFileSystem? fs = null, IReadOnlyList<SqlImpactSeed>? seeds = null) =>
        new SqlImpactFinder(fs ?? Fs(), 160).FindAsync(Ws, seeds ?? [Seed], new SqlReaderOpen(reader, null, null), SqlImpactBasis.Prod, CancellationToken.None);

    private static string Proc(string name, string body) => $"CREATE PROCEDURE {name} AS\nBEGIN\n{body}\nEND";

    private static SqlColumnUse Sel(string column) => new(Orders, column, true, false, false);

    [Fact]
    public async Task Referencing_procedure_is_exec_target()
    {
        var reader = new FakeReader()
            .Module(SeedName, "P", Proc("dbo.usp_Seed", "SELECT 1;"))
            .Module(new("dbo", "usp_A"), "P", Proc("dbo.usp_A", "EXEC dbo.usp_Seed;"))
            .Module(new("dbo", "usp_B"), "P", Proc("dbo.usp_B", "-- EXEC dbo.usp_Seed\nSELECT * FROM sys.objects WHERE name = 'usp_Seed';"))
            .Module(new("dbo", "vw_C"), "V", "CREATE VIEW dbo.vw_C AS SELECT 1 AS X")
            .Refs(SeedName, ("dbo", "usp_A", "P"), ("dbo", "usp_B", "P"), ("dbo", "vw_C", "V"));

        var result = await Run(reader);

        Assert.Equal(SqlImpactBasis.Prod, result.Basis);
        Assert.Null(result.Unavailable);
        var a = Assert.Single(result.Targets, t => t.Member == "dbo.usp_A");
        Assert.Equal(ImpactTargetKind.SqlObject, a.TargetKind);
        Assert.Equal("", a.Path);
        Assert.Equal("T1", a.SeedTaskId);
        Assert.Equal("EXEC", a.Reason);
        Assert.Equal("참조", Assert.Single(result.Targets, t => t.Member == "dbo.usp_B").Reason);
        Assert.Equal("참조", Assert.Single(result.Targets, t => t.Member == "dbo.vw_C").Reason);
    }

    [Fact]
    public async Task Exec_forms_unqualified_bracketed_and_return_value_count()
    {
        var reader = new FakeReader()
            .Module(SeedName, "P", Proc("dbo.usp_Seed", "SELECT 1;"))
            .Module(new("dbo", "usp_A"), "P", Proc("dbo.usp_A", "EXECUTE usp_Seed 1;"))
            .Module(new("dbo", "usp_B"), "P", Proc("dbo.usp_B", "DECLARE @rc int; EXEC @rc = [dbo].[usp_Seed];"))
            .Module(new("dbo", "usp_C"), "P", Proc("dbo.usp_C", "EXEC dbo.usp_Seed2;"))
            .Refs(SeedName, ("dbo", "usp_A", "P"), ("dbo", "usp_B", "P"), ("dbo", "usp_C", "P"));

        var result = await Run(reader);

        Assert.Equal(["EXEC", "EXEC", "참조"], result.Targets.OrderBy(t => t.Member, StringComparer.Ordinal).Select(t => t.Reason));
    }

    [Fact]
    public async Task Column_readers_are_narrowed_to_written_columns()
    {
        var reader = new FakeReader()
            .Module(SeedName, "P", Proc("dbo.usp_Seed", "UPDATE dbo.Orders SET Status = 9 WHERE OrderId = 1;"))
            .Columns(Orders, "OrderId", "Status", "Amount")
            .Refs(Orders, ("dbo", "usp_Seed", "P"), ("dbo", "usp_ReadsStatus", "P"), ("dbo", "usp_ReadsAmount", "P"), ("dbo", "usp_Star", "P"))
            .Uses(new("dbo", "usp_ReadsStatus"), Sel("STATUS"), Sel("OrderId"))
            .Uses(new("dbo", "usp_ReadsAmount"), Sel("Amount"))
            .Uses(new("dbo", "usp_Star"), new SqlColumnUse(Orders, null, false, false, true));

        var result = await Run(reader);

        Assert.Equal(["dbo.usp_ReadsStatus", "dbo.usp_Star"], result.Targets.Select(t => t.Member).Order(StringComparer.Ordinal));
        Assert.All(result.Targets, t => Assert.Equal("같은 컬럼 읽음", t.Reason));
        Assert.All(result.Targets, t => Assert.Equal(ImpactTargetKind.SqlObject, t.TargetKind));
        var share = Assert.Single(result.Tables);
        Assert.Equal("T1", share.SeedTaskId);
        Assert.True(share.Table.SameAs(Orders));
        Assert.Equal(["Status"], share.WrittenColumns);
        Assert.Equal(3, share.Modules);
        Assert.Equal(2, share.Warned);
        Assert.Equal(0, share.Omitted);
        Assert.Equal(0, share.Unverifiable);

        var rows = result.FullLists["T1|dbo.Orders"];
        Assert.Equal(3, rows.Count);
        Assert.Empty(rows.Single(r => r.Module.Name == "usp_ReadsAmount").OverlapColumns);
        Assert.Equal(["Status"], rows.Single(r => r.Module.Name == "usp_Star").OverlapColumns);
    }

    [Fact]
    public async Task Insert_without_column_list_writes_all_columns()
    {
        var reader = new FakeReader()
            .Module(SeedName, "P", Proc("dbo.usp_Seed", "INSERT INTO dbo.Orders VALUES (1, 2, 3);"))
            .Columns(Orders, "OrderId", "Status", "Amount")
            .Refs(Orders, ("dbo", "usp_ReadsAmount", "P"))
            .Uses(new("dbo", "usp_ReadsAmount"), Sel("Amount"));

        var result = await Run(reader);

        Assert.Equal(["OrderId", "Status", "Amount"], Assert.Single(result.Tables).WrittenColumns);
        Assert.Equal("dbo.usp_ReadsAmount", Assert.Single(result.Targets).Member);
    }

    [Fact]
    public async Task Warnings_capped_at_ten_sorted_by_overlap_then_name()
    {
        var reader = new FakeReader()
            .Module(SeedName, "P", Proc("dbo.usp_Seed", "UPDATE dbo.Orders SET Status = 9, Amount = 0 WHERE OrderId = 1;"))
            .Columns(Orders, "OrderId", "Status", "Amount");
        var refs = new List<(string, string, string)>();
        for (var i = 0; i < 12; i++)
        {
            var name = $"usp_R{i:00}";
            refs.Add(("dbo", name, "P"));
            // R00..R02 read both columns; the rest read one.
            reader.Uses(new("dbo", name), i < 3 ? [Sel("Status"), Sel("Amount")] : [Sel("Status")]);
        }
        reader.Refs(Orders, [.. refs]);

        var result = await Run(reader);

        Assert.Equal(SqlImpactFinder.MaxWarningsPerTable, result.Targets.Count);
        Assert.Equal(["dbo.usp_R00", "dbo.usp_R01", "dbo.usp_R02", "dbo.usp_R03", "dbo.usp_R04", "dbo.usp_R05", "dbo.usp_R06", "dbo.usp_R07", "dbo.usp_R08", "dbo.usp_R09"],
            result.Targets.Select(t => t.Member));
        var share = Assert.Single(result.Tables);
        Assert.Equal(12, share.Modules);
        Assert.Equal(10, share.Warned);
        Assert.Equal(2, share.Omitted);
        Assert.Equal(12, result.FullLists["T1|dbo.Orders"].Count);
    }

    [Fact]
    public async Task Unverifiable_candidate_counts_in_summary()
    {
        var reader = new FakeReader()
            .Module(SeedName, "P", Proc("dbo.usp_Seed", "UPDATE dbo.Orders SET Status = 9;"))
            .Columns(Orders, "OrderId", "Status")
            .Refs(Orders, ("dbo", "usp_Broken", "P"));

        var result = await Run(reader);

        Assert.Empty(result.Targets);
        var share = Assert.Single(result.Tables);
        Assert.Equal(1, share.Modules);
        Assert.Equal(0, share.Warned);
        Assert.Equal(1, share.Unverifiable);
        var row = Assert.Single(result.FullLists["T1|dbo.Orders"]);
        Assert.True(row.Unverifiable);
        Assert.Empty(row.OverlapColumns);
    }

    [Fact]
    public async Task Delete_only_table_has_summary_but_no_column_warnings()
    {
        var reader = new FakeReader()
            .Module(SeedName, "P", Proc("dbo.usp_Seed", "DELETE FROM dbo.Orders WHERE OrderId = 1;"))
            .Columns(Orders, "OrderId", "Status")
            .Refs(Orders, ("dbo", "usp_Star", "P"))
            .Uses(new("dbo", "usp_Star"), new SqlColumnUse(Orders, null, false, false, true));

        var result = await Run(reader);

        Assert.Empty(result.Targets);
        var share = Assert.Single(result.Tables);
        Assert.Empty(share.WrittenColumns);
        Assert.Equal(1, share.Modules);
        Assert.Equal(0, share.Warned);
    }

    [Fact]
    public async Task Seed_missing_on_server_is_new_object()
    {
        var reader = new FakeReader().Refs(SeedName, ("dbo", "usp_A", "P"));

        var result = await Run(reader, Fs(CallerCode));

        Assert.Equal(["T1"], result.NewObjectSeeds);
        Assert.Empty(result.Tables);
        var t = Assert.Single(result.Targets);
        Assert.Equal(ImpactTargetKind.CSharpMember, t.TargetKind);
        Assert.Equal("Repo.cs", t.Path);
        Assert.Equal("Cancel", t.Member);
        Assert.Equal(SqlNameStringFinder.Reason, t.Reason);
    }

    [Fact]
    public async Task Unavailable_db_runs_only_string_search()
    {
        var result = await new SqlImpactFinder(Fs(CallerCode), 160).FindAsync(Ws, [Seed],
            new SqlReaderOpen(null, null, "운영 SQL 접속 대상이 허용되지 않습니다"), SqlImpactBasis.Prod, CancellationToken.None);

        Assert.Null(result.Basis);
        Assert.Equal("운영 SQL 접속 대상이 허용되지 않습니다", result.Unavailable);
        Assert.All(result.Targets, t => Assert.Equal(ImpactTargetKind.CSharpMember, t.TargetKind));
        Assert.Single(result.Targets);
        Assert.Empty(result.Tables);
        Assert.Empty(result.NewObjectSeeds);
    }

    [Fact]
    public async Task Connection_failure_during_search_keeps_string_results()
    {
        var reader = new FakeReader { Throw = new SqlUnavailableException("접속 실패") };

        var result = await Run(reader, Fs(CallerCode));

        Assert.Null(result.Basis);
        Assert.Equal("접속 실패", result.Unavailable);
        Assert.Equal(ImpactTargetKind.CSharpMember, Assert.Single(result.Targets).TargetKind);
    }

    [Fact]
    public async Task Exec_and_column_hit_on_same_module_warns_once()
    {
        var reader = new FakeReader()
            .Module(SeedName, "P", Proc("dbo.usp_Seed", "UPDATE dbo.Orders SET Status = 9;"))
            .Module(new("dbo", "usp_A"), "P", Proc("dbo.usp_A", "SELECT Status FROM dbo.Orders; EXEC dbo.usp_Seed;"))
            .Columns(Orders, "OrderId", "Status")
            .Refs(SeedName, ("dbo", "usp_A", "P"))
            .Refs(Orders, ("dbo", "usp_A", "P"))
            .Uses(new("dbo", "usp_A"), Sel("Status"));

        var result = await Run(reader);

        var t = Assert.Single(result.Targets);
        Assert.Equal("EXEC", t.Reason);
        Assert.Equal(1, Assert.Single(result.Tables).Warned);
    }

    private sealed class FakeReader : ISqlDefinitionReader
    {
        private readonly List<SqlModuleDefinition> _modules = [];
        private readonly List<(SqlObjectName Target, List<SqlModuleRef> Refs)> _refs = [];
        private readonly List<(SqlObjectName Module, List<SqlColumnUse> Uses)> _uses = [];
        private readonly List<(SqlObjectName Table, string[] Columns)> _columns = [];

        public Exception? Throw { get; init; }

        public FakeReader Module(SqlObjectName name, string type, string definition)
        {
            _modules.Add(new SqlModuleDefinition(name, type, definition, true, true));
            return this;
        }

        public FakeReader Refs(SqlObjectName target, params (string Schema, string Name, string Type)[] refs)
        {
            _refs.Add((target, refs.Select(r => new SqlModuleRef(new SqlObjectName(r.Schema, r.Name), r.Type)).ToList()));
            return this;
        }

        public FakeReader Uses(SqlObjectName module, params SqlColumnUse[] uses)
        {
            _uses.Add((module, [new SqlColumnUse(Orders, null, false, false, false), .. uses]));
            return this;
        }

        public FakeReader Columns(SqlObjectName table, params string[] columns)
        {
            _columns.Add((table, columns));
            return this;
        }

        private void Check()
        {
            if (Throw is not null) throw Throw;
        }

        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct)
        {
            Check();
            return Task.FromResult(_modules.FirstOrDefault(m => m.Name.SameAs(name)));
        }

        public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct)
        {
            Check();
            return Task.FromResult<IReadOnlyList<SqlModuleRef>>(_refs.Where(r => r.Target.SameAs(name)).SelectMany(r => r.Refs).ToList());
        }

        public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct)
        {
            Check();
            var hit = _uses.FirstOrDefault(u => u.Module.SameAs(module));
            return Task.FromResult<IReadOnlyList<SqlColumnUse>?>(hit.Uses);
        }

        public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct)
        {
            Check();
            var hit = _columns.FirstOrDefault(c => c.Table.SameAs(table));
            return Task.FromResult<IReadOnlyList<string>>(hit.Columns ?? []);
        }
    }
}
