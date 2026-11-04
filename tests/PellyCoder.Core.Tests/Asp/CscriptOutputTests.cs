using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

/// <summary>Compile error lines of cscript (English and Korean Windows); runtime errors are not compile errors.</summary>
public class CscriptOutputTests
{
    private const string Script = @"C:\Users\<user>\AppData\Local\Temp\bad.vbs";

    /// <summary>Script line n is page line n (n = 1..lines); past the end clamps to the last page line.</summary>
    private static VbsScript Identity(int lines) => new("", Enumerable.Range(1, lines).ToArray(), lines);

    [Fact]
    public void Parse_Korean()
    {
        var output = Script + "(2, 1) Microsoft VBScript 컴파일 오류: 'End'가 필요합니다.\r\n";

        var d = Assert.Single(CscriptOutput.Parse(output, Script, Identity(10)));

        Assert.Equal((Script, 2, 1, "error", "VBS", "'End'가 필요합니다."), (d.File, d.Line, d.Column, d.Severity, d.Code, d.Message));
    }

    [Fact]
    public void Parse_English()
    {
        var output = "\r\n" + Script + "(3, 12) Microsoft VBScript compilation error: Expected 'Then'\r\n" +
                     Script + "(7, 1) Microsoft VBScript compilation error: Expected 'End'\r\n";

        var errors = CscriptOutput.Parse(output, Script, Identity(10));

        Assert.Equal([(3, 12, "Expected 'Then'"), (7, 1, "Expected 'End'")], errors.Select(e => (e.Line, e.Column, e.Message)));
    }

    [Fact]
    public void Parse_RuntimeError_Ignored()
    {
        var output = Script + "(4, 1) Microsoft VBScript runtime error: Object required: 'Response'\r\n" +
                     Script + "(5, 1) Microsoft VBScript 런타임 오류: 개체가 필요합니다.\r\n" +
                     "Input Error: Can not find script file\r\n";

        Assert.Empty(CscriptOutput.Parse(output, Script, Identity(10)));
    }

    [Fact]
    public void Parse_LineBeyondEnd_Clamped()
    {
        var output = Script + "(12, 1) Microsoft VBScript compilation error: Expected 'End'\n";

        var d = Assert.Single(CscriptOutput.Parse(output, Script, Identity(8)));

        Assert.Equal(8, d.Line);
    }

    [Fact]
    public void Parse_ScriptLine_MappedToPageLine()
    {
        // Script lines 1-4 come from page lines 3, 3, 7, 7; the page has 12 lines.
        var script = new VbsScript("", [3, 3, 7, 7], 12);
        var output = Script + "(4, 5) Microsoft VBScript compilation error: Expected 'Then'\n"
            + Script + "(5, 1) Microsoft VBScript compilation error: Expected 'End'\n";

        var errors = CscriptOutput.Parse(output, Script, script);

        Assert.Equal([(7, 5), (12, 1)], errors.Select(e => (e.Line, e.Column)));
    }
}
