using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlDefinitionTextTests
{
    private const int Compat = 150;
    private static string H(string s) => SqlDefinitionText.Hash(s, Compat);

    [Fact]
    public void Normalize_ignores_crlf_and_trailing_spaces() =>
        Assert.Equal(H("CREATE PROCEDURE p AS\r\nSELECT 1   \r\n"), H("\n\nALTER PROCEDURE p AS\nSELECT 1\n"));

    [Fact]
    public void Normalize_keeps_comment_and_case_changes()
    {
        Assert.NotEqual(H("CREATE PROCEDURE p AS\n-- a\nSELECT 1"), H("CREATE PROCEDURE p AS\n-- b\nSELECT 1"));
        Assert.NotEqual(H("CREATE PROCEDURE p AS select 1"), H("CREATE PROCEDURE p AS SELECT 1"));
    }

    [Fact]
    public void Normalize_unifies_create_or_alter() =>
        Assert.Equal(H("CREATE PROCEDURE p AS SELECT 1"), H("CREATE OR ALTER PROCEDURE p AS SELECT 1"));

    [Fact]
    public void ToAlter_replaces_first_create_only()
    {
        var r = SqlDefinitionText.ToAlter("CREATE PROCEDURE p AS\nBEGIN\n  CREATE TABLE #t(a int)\nEND", Compat);
        Assert.Equal("ALTER PROCEDURE p AS\nBEGIN\n  CREATE TABLE #t(a int)\nEND", r);
    }

    [Fact]
    public void ToAlter_skips_create_inside_comment()
    {
        var r = SqlDefinitionText.ToAlter("/* CREATE: 2020-01 */\n-- CREATE note\nCREATE PROCEDURE p AS SELECT 1", Compat);
        Assert.Equal("/* CREATE: 2020-01 */\n-- CREATE note\nALTER PROCEDURE p AS SELECT 1", r);
    }

    [Fact]
    public void ToAlter_keeps_create_or_alter()
    {
        const string s = "CREATE  OR\nALTER PROCEDURE p AS SELECT 1";
        Assert.Equal(s, SqlDefinitionText.ToAlter(s, Compat));
    }

    private static PullRecord Rec() =>
        new("prod", "srv", "Shop", SqlObjectName.Parse("dbo.usp_X"), "aa", new DateTimeOffset(2026, 10, 26, 9, 12, 33, TimeSpan.Zero));

    private static SqlModuleDefinition Mod(string def) =>
        new(SqlObjectName.Parse("dbo.usp_X"), "P", def, false, true);

    [Fact]
    public void RenderPullFile_matches_ssms_shape()
    {
        var text = SqlDefinitionText.RenderPullFile(Mod("CREATE PROCEDURE [dbo].[usp_X] AS\nSELECT 1\n"), Rec(), null, Compat);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(Rec().Format(), lines[0]);
        Assert.Equal(["SET ANSI_NULLS OFF", "GO", "SET QUOTED_IDENTIFIER ON", "GO", "ALTER PROCEDURE [dbo].[usp_X] AS", "SELECT 1", "GO"], lines[1..]);
    }

    [Fact]
    public void RenderPullFile_follows_crlf_of_definition()
    {
        var text = SqlDefinitionText.RenderPullFile(Mod("CREATE PROCEDURE p AS\r\nSELECT 1"), Rec(), null, Compat);
        Assert.Equal(text.Count(c => c == '\n'), text.Split("\r\n").Length - 1);
        Assert.EndsWith("GO\r\n", text);
    }

    [Fact]
    public void RenderPullFile_adds_dev_differs_line()
    {
        var text = SqlDefinitionText.RenderPullFile(Mod("CREATE PROCEDURE p AS SELECT 1"), Rec(), "abc", Compat);
        Assert.Equal("-- pelly pull: dev differs (sha256=abc)", text.Split('\n')[1]);
    }
}
