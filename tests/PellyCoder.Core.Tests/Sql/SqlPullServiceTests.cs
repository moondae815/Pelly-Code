using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlPullServiceTests
{
    private const int Compat = 160;
    private const string Out = "/w/db/pulled";
    private const string Definition = "CREATE PROCEDURE [dbo].[usp_X]\nAS\nSELECT 1\n";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T09:12:33Z");
    private static readonly SqlEndpoint Endpoint = new("proddb01", "proddb01", "Shop");

    private sealed class FakeReader(bool canView = true) : ISqlDefinitionReader
    {
        public Dictionary<string, SqlModuleDefinition> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Unreachable { get; set; }

        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => Task.FromResult(new SqlServerProbe("16", "Shop", canView));

        public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct)
        {
            if (Unreachable) throw new SqlUnavailableException("down");
            return Task.FromResult(Modules.GetValueOrDefault(name.Display));
        }

        public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct) => throw new NotSupportedException();

        public FakeReader With(string display, string type = "P", string? definition = Definition)
        {
            Modules[display] = new SqlModuleDefinition(SqlObjectName.Parse(display), type, definition, true, true);
            return this;
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SqlPullService Service(MockFileSystem fs) => new(fs, new FixedTime(), Compat);

    private static SqlPullRequest Request(bool force = false, PullSource source = PullSource.Prod, params string[] objects) =>
        new(objects, source, Out, force);

    [Fact]
    public async Task Writes_alter_script_with_record()
    {
        var fs = new MockFileSystem();
        var reader = new FakeReader().With("dbo.usp_X");

        var items = await Service(fs).PullAsync(Request(objects: "dbo.usp_X"), reader, Endpoint, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(items);
        Assert.Equal(PullItemStatus.Written, item.Status);
        var path = "/w/db/pulled/dbo.usp_X.sql".Replace('/', fs.Path.DirectorySeparatorChar);
        Assert.Equal(fs.Path.GetFullPath(path), fs.Path.GetFullPath(item.Path!));
        var bytes = fs.File.ReadAllBytes(item.Path!);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var record = PullRecord.TryParse(text);
        Assert.NotNull(record);
        Assert.Equal("prod", record.Source);
        Assert.Equal("proddb01", record.Server);
        Assert.Equal("Shop", record.Database);
        Assert.Equal(SqlDefinitionText.Hash(Definition, Compat), record.Sha256);
        Assert.Equal(Now, record.At);
        Assert.Contains("ALTER PROCEDURE [dbo].[usp_X]", text);
    }

    [Fact]
    public async Task Schema_defaults_to_dbo()
    {
        var fs = new MockFileSystem();
        var reader = new FakeReader().With("dbo.usp_X");

        var items = await Service(fs).PullAsync(Request(objects: "usp_X"), reader, Endpoint, null, TestContext.Current.CancellationToken);

        Assert.Equal(PullItemStatus.Written, items[0].Status);
        Assert.EndsWith("dbo.usp_X.sql", items[0].Path);
    }

    [Fact]
    public async Task Existing_file_is_not_overwritten_without_force()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/w/db/pulled/dbo.usp_X.sql", new MockFileData("mine"));
        var reader = new FakeReader().With("dbo.usp_X");

        var items = await Service(fs).PullAsync(Request(objects: "dbo.usp_X"), reader, Endpoint, null, TestContext.Current.CancellationToken);

        Assert.Equal(PullItemStatus.Exists, items[0].Status);
        Assert.Equal("mine", fs.File.ReadAllText("/w/db/pulled/dbo.usp_X.sql"));
    }

    [Fact]
    public async Task Force_overwrites()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/w/db/pulled/dbo.usp_X.sql", new MockFileData("mine"));
        var reader = new FakeReader().With("dbo.usp_X");

        var items = await Service(fs).PullAsync(Request(force: true, objects: "dbo.usp_X"), reader, Endpoint, null, TestContext.Current.CancellationToken);

        Assert.Equal(PullItemStatus.Written, items[0].Status);
        Assert.Contains("ALTER PROCEDURE", fs.File.ReadAllText("/w/db/pulled/dbo.usp_X.sql"));
    }

    [Fact]
    public async Task Unsupported_kind_fails_only_that_object()
    {
        var fs = new MockFileSystem();
        var reader = new FakeReader().With("dbo.Orders", "U", "x").With("dbo.usp_X");

        var items = await Service(fs).PullAsync(Request(objects: ["dbo.Orders", "dbo.usp_X"]), reader, Endpoint, null, TestContext.Current.CancellationToken);

        Assert.Equal([PullItemStatus.UnsupportedKind, PullItemStatus.Written], items.Select(i => i.Status));
        Assert.False(fs.File.Exists("/w/db/pulled/dbo.Orders.sql"));
    }

    [Fact]
    public async Task Missing_and_bad_names_are_reported()
    {
        var fs = new MockFileSystem();
        var reader = new FakeReader();

        var items = await Service(fs).PullAsync(Request(objects: ["dbo.nope", "a.b.c"]), reader, Endpoint, null, TestContext.Current.CancellationToken);

        Assert.Equal([PullItemStatus.NotFound, PullItemStatus.BadName], items.Select(i => i.Status));
    }

    [Fact]
    public async Task Encrypted_object_fails_with_encrypted_message()
    {
        var fs = new MockFileSystem();
        var reader = new FakeReader(canView: true).With("dbo.usp_X", definition: null);

        var items = await Service(fs).PullAsync(Request(objects: "dbo.usp_X"), reader, Endpoint, null, TestContext.Current.CancellationToken);

        Assert.Equal(PullItemStatus.Encrypted, items[0].Status);
        Assert.Contains("암호화된 객체는 받을 수 없습니다", items[0].Notices[0]);
    }

    [Fact]
    public async Task Null_definition_without_permission_says_permission()
    {
        var fs = new MockFileSystem();
        var reader = new FakeReader(canView: false).With("dbo.usp_X", definition: null);

        var items = await Service(fs).PullAsync(Request(objects: "dbo.usp_X"), reader, Endpoint, null, TestContext.Current.CancellationToken);

        Assert.Equal(PullItemStatus.NoPermission, items[0].Status);
        Assert.DoesNotContain("암호화", items[0].Notices[0]);
    }

    [Fact]
    public async Task Dev_differs_adds_line_and_notice()
    {
        var fs = new MockFileSystem();
        var prod = new FakeReader().With("dbo.usp_X");
        var dev = new FakeReader().With("dbo.usp_X", definition: Definition + "-- dev\n");

        var items = await Service(fs).PullAsync(Request(objects: "dbo.usp_X"), prod, Endpoint, dev, TestContext.Current.CancellationToken);

        Assert.Equal(PullItemStatus.Written, items[0].Status);
        Assert.Contains(items[0].Notices, n => n.Contains("개발 DB") && n.Contains("다릅니다"));
        var devHash = SqlDefinitionText.Hash(Definition + "-- dev\n", Compat);
        Assert.Contains($"-- pelly pull: dev differs (sha256={devHash})", fs.File.ReadAllText(items[0].Path!));
    }

    [Fact]
    public async Task Dev_same_has_no_line_or_notice()
    {
        var fs = new MockFileSystem();
        var prod = new FakeReader().With("dbo.usp_X");
        var dev = new FakeReader().With("dbo.usp_X");

        var items = await Service(fs).PullAsync(Request(objects: "dbo.usp_X"), prod, Endpoint, dev, TestContext.Current.CancellationToken);

        Assert.Empty(items[0].Notices);
        Assert.DoesNotContain("dev differs", fs.File.ReadAllText(items[0].Path!));
    }

    [Fact]
    public async Task Dev_missing_object_warns()
    {
        var fs = new MockFileSystem();
        var prod = new FakeReader().With("dbo.usp_X");

        var items = await Service(fs).PullAsync(Request(objects: "dbo.usp_X"), prod, Endpoint, new FakeReader(), TestContext.Current.CancellationToken);

        Assert.Equal(PullItemStatus.Written, items[0].Status);
        Assert.Contains(items[0].Notices, n => n.Contains("개발 DB에 없음 — pelly sql 검증이 실패할 수 있습니다"));
    }

    [Fact]
    public async Task Dev_unreachable_notes_not_compared()
    {
        var fs = new MockFileSystem();
        var prod = new FakeReader().With("dbo.usp_X");

        var items = await Service(fs).PullAsync(Request(objects: "dbo.usp_X"), prod, Endpoint, new FakeReader { Unreachable = true }, TestContext.Current.CancellationToken);

        Assert.Equal(PullItemStatus.Written, items[0].Status);
        Assert.Contains(items[0].Notices, n => n.Contains("개발 DB와 비교하지 못함"));
    }

    [Fact]
    public async Task Pull_from_dev_records_dev_and_never_compares()
    {
        var fs = new MockFileSystem();
        var dev = new FakeReader().With("dbo.usp_X");
        var trap = new FakeReader { Unreachable = true };

        var items = await Service(fs).PullAsync(Request(source: PullSource.Dev, objects: "dbo.usp_X"), dev, Endpoint, trap, TestContext.Current.CancellationToken);

        Assert.Empty(items[0].Notices);
        Assert.Equal("dev", PullRecord.TryParse(fs.File.ReadAllText(items[0].Path!))!.Source);
    }

    [Fact]
    public async Task Same_object_named_twice_is_pulled_once()
    {
        var fs = new MockFileSystem();
        var reader = new FakeReader().With("dbo.usp_X");

        var items = await Service(fs).PullAsync(Request(objects: ["usp_X", "dbo.usp_X", "[dbo].[USP_X]"]), reader, Endpoint, null, TestContext.Current.CancellationToken);

        Assert.Equal(PullItemStatus.Written, Assert.Single(items).Status);
    }
}
