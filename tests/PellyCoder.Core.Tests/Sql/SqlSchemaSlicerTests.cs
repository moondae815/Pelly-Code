using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Sql;

public sealed class SqlSchemaSlicerTests
{
    private sealed class CharCounter : ITokenCounter
    {
        public int Count(string text) => text.Length;
    }

    private static readonly SqlObjectName Orders = SqlObjectName.Parse("dbo.Orders");
    private static readonly SqlObjectName Customers = SqlObjectName.Parse("dbo.Customers");
    private static readonly SqlObjectName Shipped = SqlObjectName.Parse("dbo.vw_ShippedOrders");

    private static SqlBatch Batch(string text = "ALTER PROCEDURE dbo.usp_X AS SELECT 1", int start = 5, int end = 9) =>
        new(0, start, end, text, SqlObjectKind.Procedure, SqlObjectName.Parse("dbo.usp_X"), "AlterProcedure", []);

    private static SqlReferences Refs(IEnumerable<SqlObjectName> objects, params string[] columns) =>
        new(objects.Select(o => new SqlObjectReference(o, 1, 1)).ToList(),
            new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase), false, false, []);

    private static FakeSqlExecutor Fake()
    {
        var f = new FakeSqlExecutor();
        f.Schemas.Add(new SqlTableSchema(Orders, "TABLE",
            [new("OrderId", "int", false, true), new("Status", "tinyint", false, false), new("ShippedAt", "datetime2", true, false)],
            [new("IX_Orders_Status", ["Status"], false)]));
        f.Schemas.Add(new SqlTableSchema(Customers, "TABLE",
            [new("CustomerId", "int", false, true), new("Email", "nvarchar(100)", false, false), new("Note", "nvarchar(max)", true, false)],
            [new("UX_Customers_Email", ["Email"], true)]));
        f.Schemas.Add(new SqlTableSchema(Shipped, "VIEW", [new("OrderId", "int", false, false)], []));
        return f;
    }

    private static Task<PellyCoder.Core.CSharp.SliceResult> Build(FakeSqlExecutor f, SqlReferences refs, int budget, SqlBatch? b = null) =>
        SqlSchemaSlicer.BuildAsync(b ?? Batch(), refs, f, new CharCounter(), budget, 160, CancellationToken.None);

    private static string SchemaText(PellyCoder.Core.CSharp.SliceResult r) =>
        r.Slice.Included.SingleOrDefault(p => p.Kind == "schema")?.Text ?? "";

    [Fact]
    public async Task RendersTablesIndexesAndViews_InReferenceOrder()
    {
        var r = await Build(Fake(), Refs([Orders, Customers, Shipped]), 100_000);

        Assert.Equal(
            "TABLE dbo.Orders (OrderId int NOT NULL PK, Status tinyint NOT NULL, ShippedAt datetime2 NULL)\n"
            + "INDEX IX_Orders_Status (Status)\n"
            + "TABLE dbo.Customers (CustomerId int NOT NULL PK, Email nvarchar(100) NOT NULL, Note nvarchar(max) NULL)\n"
            + "UNIQUE INDEX UX_Customers_Email (Email)\n"
            + "VIEW dbo.vw_ShippedOrders (OrderId int NOT NULL)",
            SchemaText(r));
        Assert.Equal(["schema", "target"], r.Slice.Included.Select(p => p.Kind));
        Assert.Equal(1, r.Slice.Included[0].Priority);
        Assert.Equal(int.MaxValue, r.Slice.Included[1].Priority);
        Assert.Equal("T-SQL, SQL Server compatibility level 160", r.Slice.LanguageHint);
        Assert.Empty(r.Slice.Omitted);
        Assert.False(r.TargetTooLarge);
    }

    [Fact]
    public async Task OverBudget_DropsIndexesFirst_ThenUnusedColumns_ThenTables()
    {
        var f = Fake();
        var refs = Refs([Orders, Customers], "Status");
        var batch = Batch("X");
        int Used(string s) => batch.Text.Length + s.Length;

        var full = SchemaText(await Build(f, refs, 100_000, batch));

        // Stage 1: indexes only (last table's index goes first, so the budget just under full drops one).
        var noIndexes = string.Join('\n', full.Split('\n').Where(l => !l.Contains("INDEX")));
        var r1 = await Build(f, refs, Used(noIndexes), batch);
        Assert.Equal(noIndexes, SchemaText(r1));
        Assert.Equal(["schema-indexes"], r1.Slice.Omitted.Select(p => p.Kind));
        Assert.Contains("INDEX IX_Orders_Status (Status)", r1.Slice.Omitted[0].Text);

        // Stage 2: unused non-PK columns go; PK and referenced stay. Customers (last) loses columns first.
        var r2 = await Build(f, refs, Used(noIndexes) - 1, batch);
        Assert.Equal(
            "TABLE dbo.Orders (OrderId int NOT NULL PK, Status tinyint NOT NULL, ShippedAt datetime2 NULL)\n"
            + "TABLE dbo.Customers (CustomerId int NOT NULL PK)",
            SchemaText(r2));
        Assert.Equal(["schema-indexes", "schema-columns"], r2.Slice.Omitted.Select(p => p.Kind));
        Assert.Equal("TABLE dbo.Customers: Email nvarchar(100) NOT NULL, Note nvarchar(max) NULL", r2.Slice.Omitted[1].Text);

        var allReduced = "TABLE dbo.Orders (OrderId int NOT NULL PK, Status tinyint NOT NULL)\nTABLE dbo.Customers (CustomerId int NOT NULL PK)";
        var r2b = await Build(f, refs, Used(allReduced), batch);
        Assert.Equal(allReduced, SchemaText(r2b));

        // Stage 3: table lines, last first.
        var r3 = await Build(f, refs, Used(allReduced) - 1, batch);
        Assert.Equal("TABLE dbo.Orders (OrderId int NOT NULL PK, Status tinyint NOT NULL)", SchemaText(r3));
        Assert.Equal(["schema-indexes", "schema-columns", "schema-tables"], r3.Slice.Omitted.Select(p => p.Kind));
        Assert.Equal("TABLE dbo.Orders: ShippedAt datetime2 NULL", r3.Slice.Omitted[1].Text.Split('\n')[1]);
        Assert.Equal("TABLE dbo.Customers (CustomerId int NOT NULL PK)", r3.Slice.Omitted[2].Text);

        var r4 = await Build(f, refs, batch.Text.Length, batch);
        Assert.Equal(["target"], r4.Slice.Included.Select(p => p.Kind));
        Assert.False(r4.TargetTooLarge);
    }

    [Fact]
    public async Task TargetAloneOverBudget_IsTooLarge()
    {
        var r = await Build(Fake(), Refs([Orders]), 5, Batch("ALTER PROCEDURE dbo.usp_X AS SELECT 1"));

        Assert.True(r.TargetTooLarge);
        Assert.Equal(["target"], r.Slice.Included.Select(p => p.Kind));
        Assert.NotEmpty(r.Slice.Omitted);
    }

    [Fact]
    public async Task Region_IsTargetBatchLines()
    {
        var r = await Build(Fake(), Refs([Orders]), 100_000, Batch(start: 12, end: 30));

        var region = Assert.Single(r.Regions);
        Assert.Equal(12, region.Span.StartLine);
        Assert.Equal(30, region.Span.EndLine);
        Assert.Equal("target", region.Reason);
    }

    [Fact]
    public async Task ReferencedEntities_AreMergedWithoutDuplicates()
    {
        var f = Fake();
        f.Referenced["dbo.usp_X"] = [Customers, Orders];

        var r = await Build(f, Refs([Orders]), 100_000);

        var text = SchemaText(r);
        Assert.Equal(1, text.Split("TABLE dbo.Orders (").Length - 1);
        Assert.True(text.IndexOf("dbo.Orders", StringComparison.Ordinal) < text.IndexOf("dbo.Customers", StringComparison.Ordinal));
        Assert.Contains("TABLE dbo.Customers (", text);
    }

    [Fact]
    public async Task Stage1_PartialDrop_RemovesOnlyLastTablesIndexes()
    {
        var f = Fake();
        var refs = Refs([Orders, Customers]);
        var batch = Batch("X");
        var full = SchemaText(await Build(f, refs, 100_000, batch));
        var partial = full.Replace("\nUNIQUE INDEX UX_Customers_Email (Email)", "");

        var r = await Build(f, refs, batch.Text.Length + partial.Length, batch);

        Assert.Equal(partial, SchemaText(r));
        Assert.Contains("INDEX IX_Orders_Status (Status)", SchemaText(r));
        Assert.Equal("UNIQUE INDEX UX_Customers_Email (Email)", Assert.Single(r.Slice.Omitted).Text);
    }

    [Fact]
    public async Task UnusedView_IsNeverRenderedWithZeroColumns_AndGoesInStage3()
    {
        var f = Fake();
        var refs = Refs([Orders, Shipped], "Status");
        var batch = Batch("X");
        var orders = "TABLE dbo.Orders (OrderId int NOT NULL PK, Status tinyint NOT NULL)";
        // Fits only once the view is gone and Orders is reduced.
        var r = await Build(f, refs, batch.Text.Length + orders.Length, batch);

        Assert.Equal(orders, SchemaText(r));
        Assert.DoesNotContain("()", SchemaText(r));
        Assert.Equal("VIEW dbo.vw_ShippedOrders (OrderId int NOT NULL)", r.Slice.Omitted.Single(p => p.Kind == "schema-tables").Text);
    }

    [Fact]
    public async Task ReferencedProcedureWithoutSchema_IsSkipped_AndSchemaLessNameMergesWithDbo()
    {
        var f = Fake();
        f.Referenced["dbo.usp_X"] = [SqlObjectName.Parse("dbo.usp_Other"), SqlObjectName.Parse("Orders")];

        var r = await Build(f, Refs([Orders]), 100_000);

        var text = SchemaText(r);
        Assert.DoesNotContain("usp_Other", text);
        Assert.Equal(1, text.Split("TABLE dbo.Orders (").Length - 1);
    }

    [Fact]
    public async Task TargetTooLarge_LabelsOmittedSchemaAsSchema()
    {
        var r = await Build(Fake(), Refs([Orders]), 5);

        Assert.Equal(["schema"], r.Slice.Omitted.Select(p => p.Kind));
    }
}
