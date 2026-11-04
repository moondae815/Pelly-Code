using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

/// <summary>Compile error lines of cscript (English and Korean Windows); runtime errors are not compile errors.</summary>
public class CscriptOutputTests
{
    private const string Script = @"C:\Users\<user>\AppData\Local\Temp\bad.vbs";

    [Fact]
    public void Parse_Korean()
    {
        var output = Script + "(2, 1) Microsoft VBScript 컴파일 오류: 'End'가 필요합니다.\r\n";

        var d = Assert.Single(CscriptOutput.Parse(output, Script, 10));

        Assert.Equal((Script, 2, 1, "error", "VBS", "'End'가 필요합니다."), (d.File, d.Line, d.Column, d.Severity, d.Code, d.Message));
    }

    [Fact]
    public void Parse_English()
    {
        var output = "\r\n" + Script + "(3, 12) Microsoft VBScript compilation error: Expected 'Then'\r\n" +
                     Script + "(7, 1) Microsoft VBScript compilation error: Expected 'End'\r\n";

        var errors = CscriptOutput.Parse(output, Script, 10);

        Assert.Equal([(3, 12, "Expected 'Then'"), (7, 1, "Expected 'End'")], errors.Select(e => (e.Line, e.Column, e.Message)));
    }

    [Fact]
    public void Parse_RuntimeError_Ignored()
    {
        var output = Script + "(4, 1) Microsoft VBScript runtime error: Object required: 'Response'\r\n" +
                     Script + "(5, 1) Microsoft VBScript 런타임 오류: 개체가 필요합니다.\r\n" +
                     "Input Error: Can not find script file\r\n";

        Assert.Empty(CscriptOutput.Parse(output, Script, 10));
    }

    [Fact]
    public void Parse_LineBeyondEnd_Clamped()
    {
        var output = Script + "(12, 1) Microsoft VBScript compilation error: Expected 'End'\n";

        var d = Assert.Single(CscriptOutput.Parse(output, Script, 8));

        Assert.Equal(8, d.Line);
    }
}
