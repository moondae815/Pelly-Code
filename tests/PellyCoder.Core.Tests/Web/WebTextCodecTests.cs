using System.Text;
using PellyCoder.Core.Files;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class WebTextCodecTests
{
    private const string Path = "/w/site/js/order.js";
    private static readonly Encoding Cp949;
    private static readonly WebTextCodec Codec = new("cp949");

    static WebTextCodecTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949);
    }

    private static byte[] C(string s) => Cp949.GetBytes(s);
    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Decode_Bom_Wins()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. U("@charset \"euc-kr\";\n한글\n")];
        var snap = Codec.Decode(Path, bytes);
        Assert.Equal(65001, snap.Encoding.CodePage);
        Assert.True(snap.HasBom);
        Assert.Equal("@charset \"euc-kr\";\n한글\n", snap.Text);
    }

    [Fact]
    public void Decode_CharsetUtf8_Utf8()
    {
        var snap = Codec.Decode(Path, U("@charset \"utf-8\";\na{}\n"));
        Assert.Equal(65001, snap.Encoding.CodePage);
    }

    [Theory]
    [InlineData("euc-kr")]
    [InlineData("ks_c_5601-1987")]
    [InlineData("cp949")]
    [InlineData("windows-949")]
    public void Decode_CharsetEucKr_Cp949(string name)
    {
        var snap = Codec.Decode(Path, C($"@charset \"{name}\";\n/* 한글 */\n"));
        Assert.Equal(949, snap.Encoding.CodePage);
        Assert.Contains("한글", snap.Text);
    }

    [Fact]
    public void Decode_CharsetOther_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Codec.Decode(Path, U("@charset \"shift_jis\";\na{}\n")));
        Assert.Contains("@charset \"shift_jis\"은 지원하지 않습니다", ex.Message);
    }

    [Fact]
    public void Decode_CodePageTextInJs_Ignored()
    {
        var snap = Codec.Decode(Path, C("var s = \"CodePage=65001\";\n// 한글\n"));
        Assert.Equal(949, snap.Encoding.CodePage);
        Assert.Contains("한글", snap.Text);
    }

    [Fact]
    public void Decode_PureAscii_UsesDefault()
    {
        Assert.Equal(949, new WebTextCodec("cp949").Decode(Path, U("var a = 1;\n")).Encoding.CodePage);
        Assert.Equal(65001, new WebTextCodec("utf-8").Decode(Path, U("var a = 1;\n")).Encoding.CodePage);
    }

    [Fact]
    public void Decode_Utf16_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Codec.Decode(Path, [0xFF, 0xFE, 0x61, 0x00]));
        Assert.Contains("UTF-16 파일", ex.Message);
    }

    [Fact]
    public void Encode_MixedNewlines_OutsideBytesIdentical()
    {
        byte[] raw = C("var a = 1;\r\nvar b = 2;\nvar c = 3;\r\nvar d = 4;\n");
        var snap = Codec.Decode(Path, raw);
        string edited = snap.Text.Replace("var c = 3;", "var c = \"한글\";");

        byte[] written = Codec.Encode(snap, edited);

        byte[] expected = C("var a = 1;\r\nvar b = 2;\nvar c = \"한글\";\r\nvar d = 4;\n");
        Assert.Equal(expected, written);
    }

    [Fact]
    public void Encode_NoFinalNewline_Kept()
    {
        var snap = Codec.Decode(Path, C("a{}\nb{}"));
        byte[] written = Codec.Encode(snap, "a{}\nc{}");
        Assert.Equal(C("a{}\nc{}"), written);
    }

    [Fact]
    public void Encode_Unencodable_Throws()
    {
        var snap = Codec.Decode(Path, C("var a = '한';\n"));
        Assert.Throws<UnencodableTextException>(() => Codec.Encode(snap, "var a = '\U0001F600';\n"));
    }
}
