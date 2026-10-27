using PellyCoder.Core.Fsm;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

/// <summary>
/// The pull record refresh of a file that is production already (the user deployed their edit): only the record line changes,
/// and the safety guard accepts exactly that line outside the target batch.
/// </summary>
public class SqlRecordRefreshTests
{
    private const int Compat = 160;
    private const string OldHash = "00aa";
    private const string ProdHash = "ffee";
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 1, 2, 3, TimeSpan.Zero);

    private const string StaleLine = "-- pelly pull: source=prod server=proddb01 database=Shop object=[dbo].[usp_P] sha256=" + OldHash + " at=2026-10-26T09:12:33Z";
    private const string FreshLine = "-- pelly pull: source=prod server=proddb01 database=Shop object=[dbo].[usp_P] sha256=" + ProdHash + " at=2026-10-27T01:02:03Z";
    private const string Rest =
        "-- pelly pull: dev differs (sha256=1234)\nSET ANSI_NULLS ON\nGO\nALTER PROCEDURE dbo.usp_P\nAS\nSELECT 1\nGO\n";
    private const string File = StaleLine + "\n" + Rest;

    private static SqlRecordRefresh Refresh() => SqlRecordRefresh.For(File, PullRecord.TryParse(File)!, ProdHash, Now)!;

    private static SqlRunState State(string text)
    {
        var script = SqlBatchSplitter.Split(text, Compat);
        var target = script.ObjectBatches[0];
        return new SqlRunState(script, target.Index, target.ObjectName!, target.ObjectKind!.Value, target.StatementType!,
            script.SettingsBefore(target.Index), Compat);
    }

    [Fact]
    public void Refreshed_line_keeps_source_server_database_object_and_takes_prod_hash_and_now()
    {
        var refresh = Refresh();
        Assert.Equal(StaleLine, refresh.StaleLine);
        Assert.Equal(FreshLine, refresh.RefreshedLine);
    }

    [Fact]
    public void For_keeps_the_server_spelling_and_brackets_the_object()
    {
        var line = "-- pelly pull: source=prod server=PRODDB01 database=Shop object=dbo.usp_P sha256=" + OldHash + " at=2026-10-26T09:12:33Z";
        var text = line + "\nGO\n";
        var refresh = SqlRecordRefresh.For(text, PullRecord.TryParse(text)!, ProdHash, Now)!;
        Assert.Equal("-- pelly pull: source=prod server=PRODDB01 database=Shop object=[dbo].[usp_P] sha256=" + ProdHash + " at=2026-10-27T01:02:03Z",
            refresh.RefreshedLine);
    }

    [Fact]
    public void ApplyTo_replaces_only_the_first_line()
    {
        Assert.Equal(FreshLine + "\n" + Rest, Refresh().ApplyTo(File));
    }

    [Fact]
    public void ApplyTo_leaves_a_text_whose_first_line_is_not_the_stale_record()
    {
        var refreshed = FreshLine + "\n" + Rest;
        Assert.Equal(refreshed, Refresh().ApplyTo(refreshed));
        Assert.Equal(Rest, Refresh().ApplyTo(Rest));
    }

    [Fact]
    public void Guard_accepts_the_refreshed_record_with_a_target_edit()
    {
        var state = State(File);
        Assert.True(state.RefreshRecord(Refresh(), File));
        var patched = state.EditBase(File).Replace("SELECT 1", "SELECT 2");

        var check = SqlSafetyGuard.Check(state.OriginalScript, state.TargetIndex, patched, Compat);

        Assert.True(check.Ok, check.Reason);
        Assert.StartsWith(FreshLine + "\n", patched);
    }

    [Fact]
    public void Guard_rejects_the_old_record_once_a_refresh_is_pending()
    {
        var state = State(File);
        Assert.True(state.RefreshRecord(Refresh(), File));

        var check = SqlSafetyGuard.Check(state.OriginalScript, state.TargetIndex, File.Replace("SELECT 1", "SELECT 2"), Compat);

        Assert.False(check.Ok);
    }

    [Theory]
    [InlineData("SET ANSI_NULLS ON", "SET ANSI_NULLS OFF")]
    [InlineData("-- pelly pull: dev differs (sha256=1234)", "-- pelly pull: dev differs (sha256=9999)")]
    [InlineData("sha256=" + ProdHash, "sha256=abcd")]
    public void Guard_still_rejects_any_other_change_outside_the_target(string from, string to)
    {
        var state = State(File);
        Assert.True(state.RefreshRecord(Refresh(), File));
        var patched = state.EditBase(File).Replace("SELECT 1", "SELECT 2").Replace(from, to);

        var check = SqlSafetyGuard.Check(state.OriginalScript, state.TargetIndex, patched, Compat);

        Assert.False(check.Ok);
        Assert.Equal("text outside the target object batch changed. Only the target object may change.", check.Reason);
    }

    [Fact]
    public void No_refresh_when_the_record_is_inside_the_target_batch()
    {
        // The target batch starts on the record line: pelly never rewrites text inside the batch the model edits.
        var text = StaleLine + "\nALTER PROCEDURE dbo.usp_P\nAS\nSELECT 1\nGO\n";
        var state = State(text);
        var refresh = SqlRecordRefresh.For(text, PullRecord.TryParse(text)!, ProdHash, Now)!;

        Assert.False(state.RefreshRecord(refresh, text));
        Assert.Null(state.RecordRefresh);
        Assert.Equal(text, state.EditBase(text));
    }
}
