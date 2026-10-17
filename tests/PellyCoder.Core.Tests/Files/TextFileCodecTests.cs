using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Tests.Files;

public sealed class TextFileCodecTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pelly-codec-").FullName;
    private readonly FileSystem _fs = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(byte[] bytes, string name = "f.txt")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static TextFileCodecTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static byte[] Sample(string kind)
    {
        const string text = "// 주문 처리\r\nint a = 1; // 한글 주석\r\n\r\nreturn a;\r\n";
        return kind switch
        {
            "utf8" => new UTF8Encoding(false).GetBytes(text),
            "utf8bom" => [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. new UTF8Encoding(false).GetBytes(text)],
            "cp949" => Encoding.GetEncoding(949).GetBytes(text),
            "utf16le" => [.. new byte[] { 0xFF, 0xFE }, .. new UnicodeEncoding(false, false).GetBytes(text)],
            _ => throw new ArgumentException(kind),
        };
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("cp949")]
    [InlineData("utf16le")]
    public void RoundTrip_UnchangedText_IsByteIdentical(string kind)
    {
        byte[] original = Sample(kind);
        var snapshot = TextFileCodec.Read(_fs, Write(original));

        Assert.Equal(original, TextFileCodec.Encode(snapshot, snapshot.Text));
        Assert.Equal("\r\n", snapshot.NewLine);
        Assert.DoesNotContain('\r', snapshot.Text);
    }

    [Fact]
    public void Read_DetectsEncodingsInDesignOrder()
    {
        var bom = TextFileCodec.Read(_fs, Write(Sample("utf8bom"), "a"));
        Assert.True(bom.HasBom);
        Assert.Equal(65001, bom.Encoding.CodePage);

        var plain = TextFileCodec.Read(_fs, Write(Sample("utf8"), "b"));
        Assert.False(plain.HasBom);
        Assert.Equal(65001, plain.Encoding.CodePage);

        var cp = TextFileCodec.Read(_fs, Write(Sample("cp949"), "c"));
        Assert.False(cp.HasBom);
        Assert.Equal(949, cp.Encoding.CodePage);
        Assert.Contains("한글 주석", cp.Text);
    }

    [Fact]
    public void Read_ComputesSha256OfDiskBytes()
    {
        byte[] bytes = Sample("cp949");
        var snapshot = TextFileCodec.Read(_fs, Write(bytes));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), snapshot.Sha256);
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\n", "\r\n")]
    [InlineData("a\nb\nc\r\n", "\n")]
    public void Read_NewLine_IsMajority(string content, string expected)
    {
        var snapshot = TextFileCodec.Read(_fs, Write(new UTF8Encoding(false).GetBytes(content)));
        Assert.Equal(expected, snapshot.NewLine);
    }

    [Fact]
    public void EndsWithNewLine_IsPreserved()
    {
        byte[] original = "a\nb"u8.ToArray();
        var snapshot = TextFileCodec.Read(_fs, Write(original));

        Assert.False(snapshot.EndsWithNewLine);
        Assert.Equal(original, TextFileCodec.Encode(snapshot, snapshot.Text));
        // 모델이 끝 줄바꿈을 붙여도 원본 규약을 따른다.
        Assert.Equal(original, TextFileCodec.Encode(snapshot, "a\nb\n"));
    }

    [Fact]
    public void Encode_Cp949_UnencodableCharacter_Throws()
    {
        var snapshot = TextFileCodec.Read(_fs, Write(Sample("cp949")));
        var ex = Assert.Throws<UnencodableTextException>(() => TextFileCodec.Encode(snapshot, "한글\nx = \"😀\";\n"));
        Assert.Equal(2, ex.Line);
    }
}
