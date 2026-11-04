using System.Text;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Tests.Asp;

public class AspTextCodecTests
{
    private const string Path = "/w/site/order.asp";
    private static readonly Encoding Cp949;
    private static readonly AspTextCodec Codec = new("cp949");

    static AspTextCodecTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949);
    }

    private static byte[] C(string s) => Cp949.GetBytes(s);
    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Decode_Bom_Wins()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. U("<%@ CodePage=949 %>\n한글\n")];

        var snap = Codec.Decode(Path, bytes);

        Assert.Equal(65001, snap.Encoding.CodePage);
        Assert.True(snap.HasBom);
        Assert.Equal("<%@ CodePage=949 %>\n한글\n", snap.Text);
        Assert.Equal(bytes, snap.Raw);
    }

    [Fact]
    public void Decode_CodePage65001_Utf8()
    {
        var snap = Codec.Decode(Path, U("<%@ Language=VBScript CodePage=65001 %>\r\n<% x = 1 %>\r\n"));

        Assert.Equal(65001, snap.Encoding.CodePage);
        Assert.False(snap.HasBom);
    }

    [Fact]
    public void Decode_CodePage949_Cp949()
    {
        var snap = new AspTextCodec("utf-8").Decode(Path, U("<%@ LANGUAGE=\"VBSCRIPT\" CODEPAGE=\"949\" %>\n<% x = 1 %>\n"));

        Assert.Equal(949, snap.Encoding.CodePage);
    }

    [Fact]
    public void Decode_CodePageOther_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Codec.Decode(Path, U("<%@ CodePage=1252 %>\n")));

        Assert.Contains("1252", ex.Message);
    }

    [Fact]
    public void Decode_NonAsciiValidUtf8_Utf8()
    {
        var snap = Codec.Decode(Path, U("<% ' 주문 %>\n"));

        Assert.Equal(65001, snap.Encoding.CodePage);
        Assert.Equal("<% ' 주문 %>\n", snap.Text);
    }

    [Fact]
    public void Decode_NonAsciiInvalidUtf8_Cp949()
    {
        var snap = new AspTextCodec("utf-8").Decode(Path, C("<% ' 주문 %>\n"));

        Assert.Equal(949, snap.Encoding.CodePage);
        Assert.Equal("<% ' 주문 %>\n", snap.Text);
    }

    [Fact]
    public void Decode_PureAscii_UsesDefault()
    {
        byte[] bytes = U("<% x = 1 %>\n");

        Assert.Equal(949, Codec.Decode(Path, bytes).Encoding.CodePage);
        Assert.Equal(65001, new AspTextCodec("utf-8").Decode(Path, bytes).Encoding.CodePage);
    }

    [Fact]
    public void Decode_CodePage949ButUtf8Korean_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Codec.Decode(Path, U("<%@ CodePage=949 %>\n<% ' 한글 %>\n")));
    }

    [Fact]
    public void Decode_Utf16_Throws()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<% x = 1 %>")];

        var ex = Assert.Throws<InvalidDataException>(() => Codec.Decode(Path, bytes));

        Assert.Contains("UTF-16 ASP 파일은 지원하지 않습니다", ex.Message);
    }

    [Fact]
    public void Encode_PureAsciiCp949_KoreanWrittenAsCp949()
    {
        var snap = Codec.Decode(Path, U("<%\nx = 1\n%>\n"));

        byte[] written = Codec.Encode(snap, "<%\nx = 1\nResponse.Write \"취소\"\n%>\n");

        Assert.Equal(C("<%\nx = 1\nResponse.Write \"취소\"\n%>\n"), written);
    }

    [Fact]
    public void Encode_MixedNewlines_OutsideBytesIdentical()
    {
        byte[] original = [.. C("<% ' 주문\r\n"), .. C("a = 1\n"), .. C("b = \"가격\"\r\n"), .. C("c = 3\n"), .. C("%>\r\n")];
        var snap = Codec.Decode(Path, original);
        Assert.False(snap.MixedNewLines);
        Assert.Equal("\r\n", snap.NewLine);

        byte[] written = Codec.Encode(snap, snap.Text.Replace("b = \"가격\"", "b = \"수정\""));

        byte[] expected = [.. C("<% ' 주문\r\n"), .. C("a = 1\n"), .. C("b = \"수정\"\r\n"), .. C("c = 3\n"), .. C("%>\r\n")];
        Assert.Equal(expected, written);
    }

    [Fact]
    public void Encode_NoFinalNewline_Kept()
    {
        var snap = Codec.Decode(Path, C("' 주문\r\nb"));

        byte[] written = Codec.Encode(snap, "' 주문\nc\n");

        Assert.Equal(C("' 주문\r\nc"), written);
    }

    [Fact]
    public void Encode_Bom_Kept()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. U("a\nb\n")];
        var snap = Codec.Decode(Path, original);

        byte[] written = Codec.Encode(snap, "a\n한글\n");

        Assert.Equal([0xEF, 0xBB, 0xBF, .. U("a\n한글\n")], written);
    }

    [Fact]
    public void Encode_Unencodable_Throws()
    {
        var snap = Codec.Decode(Path, C("' 주문\r\nx = 1\r\ny = 2\r\n"));

        var ex = Assert.Throws<UnencodableTextException>(() => Codec.Encode(snap, "' 주문\nx = 1\ny = \"😀\"\n"));

        Assert.Equal(3, ex.Line);
    }

    [Fact]
    public void Encode_InsertedLines_UseMajorityOfReplaced()
    {
        var snap = Codec.Decode(Path, C("h\r\nx\ny\nz\r\nt\r\n"));
        Assert.Equal("\r\n", snap.NewLine);

        byte[] written = Codec.Encode(snap, "h\nx1\nx2\ny1\nz\nt\n");

        Assert.Equal(C("h\r\nx1\nx2\ny1\nz\r\nt\r\n"), written);
    }

    [Fact]
    public void Encode_PureInsertion_UsesFileNewLine()
    {
        var snap = Codec.Decode(Path, C("h\r\nx\nz\r\n"));

        byte[] written = Codec.Encode(snap, "h\nx\nnew\nz\n");

        Assert.Equal(C("h\r\nx\nnew\r\nz\r\n"), written);
    }
}
