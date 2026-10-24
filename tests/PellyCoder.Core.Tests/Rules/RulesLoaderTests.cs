using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Rules;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Rules;

public class RulesLoaderTests
{
    private static readonly Workspace Ws = new("/w", "/w/.pelly");

    private static MockFileSystem WithFile(string path, byte[] bytes)
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>(), "/w");
        fs.AddFile(path, new MockFileData(bytes));
        return fs;
    }

    [Fact]
    public void Load_NoFile_Empty()
    {
        var r = RulesLoader.Load(new MockFileSystem(new Dictionary<string, MockFileData>(), "/w"), Ws);
        Assert.Null(r.Path);
        Assert.Empty(r.Rules);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Load_NoSection_Empty()
    {
        var r = RulesLoader.Load(WithFile("/w/AGENTS.md", "# Hi\n- [a] x\n"u8.ToArray()), Ws);
        Assert.Empty(r.Rules);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Load_CrlfWithUtf8Bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("# A\r\n## Pelly rules\r\n- [a] 한글 | forbid: x+\r\n")).ToArray();
        var r = RulesLoader.Load(WithFile("/w/AGENTS.md", bytes), Ws);
        Assert.Empty(r.Errors);
        var rule = Assert.Single(r.Rules);
        Assert.Equal("한글", rule.Text);
        Assert.Equal("x+", rule.Forbid);
        Assert.EndsWith("AGENTS.md", r.Path);
    }

    [Fact]
    public void Load_Cp949()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(949).GetBytes("## Pelly rules\n- [a] 주석은 한국어로\n");
        var r = RulesLoader.Load(WithFile("/w/AGENTS.md", bytes), Ws);
        Assert.Empty(r.Errors);
        Assert.Equal("주석은 한국어로", Assert.Single(r.Rules).Text);
    }

    [Fact]
    public void Load_OverSizeLimit_FileLevelError()
    {
        var r = RulesLoader.Load(WithFile("/w/AGENTS.md", new byte[RulesLoader.MaxBytes + 1]), Ws);
        Assert.Empty(r.Rules);
        var e = Assert.Single(r.Errors);
        Assert.Equal(0, e.Line);
    }

    [Fact]
    public void Load_SubfolderAgentsMdIgnored()
    {
        var r = RulesLoader.Load(WithFile("/w/src/AGENTS.md", "## Pelly rules\n- [a] x\n"u8.ToArray()), Ws);
        Assert.Null(r.Path);
        Assert.Empty(r.Rules);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Load_ParseError_NoRulesAndErrorLine()
    {
        var r = RulesLoader.Load(WithFile("/w/AGENTS.md", "## Pelly rules\n- [a] ok\n- broken\n"u8.ToArray()), Ws);
        Assert.Empty(r.Rules);
        Assert.Equal(3, Assert.Single(r.Errors).Line);
    }
}
