using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class PullRecordTests
{
    [Fact]
    public void Format_then_TryParse_roundtrips()
    {
        var rec = new PullRecord("prod", "proddb01", "Shop", SqlObjectName.Parse("dbo.usp_X"), "0a1b",
            new DateTimeOffset(2026, 10, 26, 9, 12, 33, TimeSpan.Zero));
        var line = rec.Format();
        Assert.Equal("-- pelly pull: source=prod server=proddb01 database=Shop object=[dbo].[usp_X] sha256=0a1b at=2026-10-26T09:12:33Z", line);
        Assert.Equal(rec, PullRecord.TryParse(line + "\nSET ANSI_NULLS ON\nGO\n"));
    }

    [Fact]
    public void TryParse_returns_null_for_ssms_header() =>
        Assert.Null(PullRecord.TryParse("/****** Object:  StoredProcedure [dbo].[usp_X] ******/\nSET ANSI_NULLS ON\n"));

    [Fact]
    public void TryParse_reads_bracketed_object()
    {
        var rec = PullRecord.TryParse("-- pelly pull: source=prod server=s database=d object=[dbo].[usp X] sha256=ab at=2026-10-26T09:12:33Z\n");
        Assert.NotNull(rec);
        Assert.Equal(new SqlObjectName("dbo", "usp X"), rec!.Object);
    }

    [Fact]
    public void Format_escapes_closing_bracket()
    {
        var rec = new PullRecord("prod", "s", "d", new SqlObjectName("dbo", "a]b"), "ab", DateTimeOffset.UnixEpoch);
        Assert.Equal(rec, PullRecord.TryParse(rec.Format()));
    }

    [Fact]
    public void TryParse_skips_leading_bom()
    {
        var rec = PullRecord.TryParse("\uFEFF-- pelly pull: source=prod server=s database=d object=[dbo].[usp_X] sha256=ab at=2026-10-26T09:12:33Z\nGO\n");
        Assert.NotNull(rec);
        Assert.Equal(new SqlObjectName("dbo", "usp_X"), rec!.Object);
    }
}
