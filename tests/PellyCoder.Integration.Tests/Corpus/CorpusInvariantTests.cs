using System.Text;
using System.Xml.Linq;

namespace PellyCoder.Integration.Tests.Corpus;

/// <summary>
/// 코퍼스 파일이 도구·git에 의해 바뀌지 않았는지(인코딩, 줄바꿈, 프로젝트 형식) 바이트 수준으로 확인한다.
/// </summary>
public class CorpusInvariantTests
{
    static CorpusInvariantTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void LegacyProjects_HaveNoSdkAttribute()
    {
        var projects = Directory.GetFiles(CorpusPaths.Get("Legacy"), "*.csproj", SearchOption.AllDirectories);
        Assert.Equal(4, projects.Length);
        Assert.All(projects, p => Assert.Null(XDocument.Load(p).Root!.Attribute("Sdk")));
    }

    [Fact]
    public void ModernProjects_HaveSdkAttribute()
    {
        var projects = Directory.GetFiles(CorpusPaths.Get("Modern"), "*.csproj", SearchOption.AllDirectories);
        Assert.Equal(3, projects.Length);
        Assert.All(projects, p => Assert.NotNull(XDocument.Load(p).Root!.Attribute("Sdk")));
    }

    [Fact]
    public void LegacyCsFiles_UseCrLfOnly()
    {
        var files = Directory.GetFiles(CorpusPaths.Get("Legacy"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToArray();
        Assert.NotEmpty(files);
        Assert.All(files, f =>
        {
            var bytes = File.ReadAllBytes(f);
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\n')
                    Assert.True(i > 0 && bytes[i - 1] == (byte)'\r', $"{f}: 단독 LF가 있음 (offset {i})");
            }
        });
    }

    [Theory]
    [InlineData("Legacy/Shop.Web/Services/OrderService.cs")]
    [InlineData("Legacy/Shop.Batch/SettlementBatch.cs")]
    public void Cp949Files_AreNotValidUtf8_AndDecodeAsCp949(string path)
    {
        var bytes = File.ReadAllBytes(CorpusPaths.Get(path));
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        Assert.Throws<DecoderFallbackException>(() => strictUtf8.GetString(bytes));
        Assert.Contains("주문", Encoding.GetEncoding(949).GetString(bytes));
    }

    [Fact]
    public void BuildReport_IsAtLeast200Lines()
    {
        var text = File.ReadAllText(CorpusPaths.Get("Legacy/Shop.Batch/SettlementBatch.cs"), Encoding.GetEncoding(949));
        Assert.True(MemberLength(text, "string BuildReport(") >= 200);
    }

    /// <summary>시그니처 줄부터 같은 들여쓰기의 닫는 중괄호 줄까지의 줄 수.</summary>
    internal static int MemberLength(string text, string signature)
    {
        var lines = text.Split("\r\n");
        var start = Array.FindIndex(lines, l => l.Contains(signature));
        Assert.True(start >= 0, $"{signature} 없음");
        var indent = lines[start].Length - lines[start].TrimStart().Length;
        var closing = new string(' ', indent) + "}";
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i] == closing)
                return i - start + 1;
        }

        throw new InvalidOperationException($"{signature}의 끝을 찾을 수 없음");
    }
}
