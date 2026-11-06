using System.IO.Abstractions.TestingHelpers;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>The whole ASP pipeline (CreateAsp) on a small mock site with a fake model; cscript is skipped (not Windows).</summary>
public sealed class AspOrchestratorTests
{
    // A regression must fail the test, not hang the run.
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(60);

    private const string Root = "/w";
    private const string PagePath = Root + "/order.asp";
    private const string Banner = "ASP는 실행해서 확인하지 않았습니다. 화면에서 직접 확인하십시오.";

    private const string Page =
        "<%@ Language=\"VBScript\" %>\n" +        // 1
        "<%\n" +                                    // 2
        "Option Explicit\n" +                       // 3
        "Dim total, cmd\n" +                        // 4
        "Function GetTotal(qty, price)\n" +         // 5
        "  GetTotal = qty * price\n" +              // 6
        "End Function\n" +                          // 7
        "total = GetTotal(2, 3)\n" +                // 8
        "cmd.CommandText = \"dbo.usp_GetOrder\"\n" + // 9
        "cmd.CommandType = 4\n" +                   // 10
        "Response.Write total\n" +                  // 11
        "%>\n";                                     // 12

    private const string Body = "  GetTotal = qty * price";
    private const string Good = "  GetTotal = qty * price + 1";

    private readonly MockFileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly PellyOptions _options = new();

    static AspOrchestratorTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public AspOrchestratorTests()
    {
        _fs.AddDirectory(Root + "/.git"); // pins the workspace root
    }

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private byte[] Prepare(string text = Page)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        _fs.AddFile(PagePath, new MockFileData(bytes));
        return bytes;
    }

    private Task<int> Run(string? proc = "GetTotal", TextSpanInfo? lines = null, bool yes = true, int? maxRetries = null) =>
        FsmOrchestratorFactory.CreateAsp(_fs, _backend, _ui, _options, TimeProvider.System, _processes, null, () => false)
            .RunAsync(new RunRequest(PagePath, null, proc, null, "add one", yes, false, maxRetries, Kind: EditKind.Asp, Lines: lines),
                TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    private JsonElement Summary()
    {
        var file = Assert.Single(_fs.Directory.GetFiles(Root + "/.pelly/runs", "summary.json", SearchOption.AllDirectories));
        return JsonDocument.Parse(_fs.File.ReadAllText(file)).RootElement;
    }

    [Fact]
    public async Task HappyPath_Procedure_Exit0_SummaryHasAspBlock()
    {
        Prepare();
        _backend.Enqueue(Block(Body, Good));

        var exit = await Run();

        Assert.Equal(0, exit);
        Assert.Equal(Page.Replace(Body, Good), _fs.File.ReadAllText(PagePath));
        var summary = Summary();
        Assert.Equal("Asp", summary.GetProperty("kind").GetString());
        Assert.Equal("asp-v2", summary.GetProperty("promptVersion").GetString());
        var asp = summary.GetProperty("asp");
        Assert.Equal("Procedure", asp.GetProperty("unit").GetString());
        Assert.Equal("CP949", asp.GetProperty("encoding").GetString());
        var checks = asp.GetProperty("checks");
        Assert.Equal("passed", checks.GetProperty("Structure").GetString());
        Assert.Equal("passed", checks.GetProperty("Safety").GetString());
        Assert.Equal("passed", checks.GetProperty("Encoding").GetString());
        Assert.Equal("passed", checks.GetProperty("AspNames").GetString());
        Assert.Equal("skipped", checks.GetProperty("AspCompile").GetString());
        Assert.Equal("skipped", checks.GetProperty("AspSp").GetString());
        Assert.Equal("passed", checks.GetProperty("Logic").GetString());
        Assert.Equal("dbo.usp_GetOrder", Assert.Single(asp.GetProperty("storedProcedures").EnumerateArray()).GetString());
        Assert.Equal(0, asp.GetProperty("includesFound").GetInt32());
        Assert.Equal(0, asp.GetProperty("includesMissing").GetInt32());
        Assert.False(asp.GetProperty("executed").GetBoolean());
    }

    [Fact]
    public async Task StructureError_ThenFixed_Exit0()
    {
        Prepare();
        _backend.Enqueue(Block(Body, "  If qty > 0 Then\n" + Good)); // unclosed If: rejected before any write
        _backend.Enqueue("Close the If.</think>", evalCount: 10);
        _backend.Enqueue(Block(Body, Good));

        var exit = await Run();

        Assert.Equal(0, exit);
        Assert.Contains("ASP STRUCTURE CHECK FAILED", _backend.Requests[1].RawPrompt);
        Assert.Equal(Page.Replace(Body, Good), _fs.File.ReadAllText(PagePath));
    }

    [Fact]
    public async Task RetriesExhausted_Exit1_RestoredBytes()
    {
        var original = Prepare();
        const string unknown = "  GetTotal = qty * pricee"; // undeclared under Option Explicit
        _backend.Enqueue(Block(Body, unknown));
        _backend.Enqueue("Use a declared name.</think>", evalCount: 10);
        _backend.Enqueue(Block(unknown, unknown + " + 0"));

        var exit = await Run(maxRetries: 1);

        Assert.Equal(1, exit);
        Assert.Equal(original, _fs.File.ReadAllBytes(PagePath));
        Assert.Equal(FailureKinds.AspUnknownNames, Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task SafetyExhausted_Exit5_Restored()
    {
        var original = Prepare();
        const string unsafeLine = "  Execute \"GetTotal = 0\"";
        _backend.Enqueue(Block(Body, unsafeLine));
        _backend.Enqueue("Do not run code.</think>", evalCount: 10);
        _backend.Enqueue(Block(Body, unsafeLine));

        var exit = await Run(maxRetries: 1);

        Assert.Equal(5, exit);
        Assert.Equal(original, _fs.File.ReadAllBytes(PagePath));
        var asp = Summary().GetProperty("asp");
        Assert.Equal("failed", asp.GetProperty("checks").GetProperty("Safety").GetString());
    }

    [Fact]
    public async Task Rejected_Exit2_Restored()
    {
        var original = Prepare();
        _ui.IsInteractive = true;
        _ui.ConfirmAnswers.Enqueue(false);
        _backend.Enqueue(Block(Body, Good));

        var exit = await Run(yes: false);

        Assert.Equal(2, exit);
        Assert.Equal(original, _fs.File.ReadAllBytes(PagePath));
        Assert.Contains(Banner, _ui.Warnings); // shown before the question
    }

    [Fact]
    public async Task Review_ShowsSkippedChecksAndBanner()
    {
        Prepare();
        _backend.Enqueue(Block(Body, Good));

        var exit = await Run(yes: true);

        Assert.Equal(0, exit);
        var line = Assert.Single(_ui.Warnings, w => w.StartsWith("검사: ", StringComparison.Ordinal));
        Assert.Equal(
            "검사: 구조 통과, 안전 통과, 인코딩 통과, 이름 통과, 컴파일 건너뜀(Windows가 아님), "
            + "SP 건너뜀(개발 DB 연결(sql.connectionString)이 설정되지 않았습니다), 논리 통과", line);
        Assert.Contains(Banner, _ui.Warnings);
        Assert.True(_ui.Warnings.IndexOf(line) < _ui.Warnings.IndexOf(Banner));
        var messages = _fs.File.ReadAllText(Assert.Single(_fs.Directory.GetFiles(Root + "/.pelly/runs", "messages.txt", SearchOption.AllDirectories)));
        Assert.Contains(Banner, messages);
    }

    [Fact]
    public async Task Review_LogicWarning_ShownAndApproved()
    {
        Prepare();
        const string checkAndConvert = "  If IsNumeric(qty) And CLng(qty) > 0 Then GetTotal = qty * price";
        _backend.Enqueue(Block(Body, checkAndConvert));

        var exit = await Run(yes: true);

        Assert.Equal(0, exit); // a warning, never a rejection
        Assert.Equal(Page.Replace(Body, checkAndConvert), _fs.File.ReadAllText(PagePath));
        var line = Assert.Single(_ui.Warnings, w => w.StartsWith("검사: ", StringComparison.Ordinal));
        Assert.EndsWith(", 논리 경고(And/Or는 단축 평가를 하지 않습니다: qty를 같은 조건에서 검사·변환합니다)", line);
        Assert.Equal("warned", Summary().GetProperty("asp").GetProperty("checks").GetProperty("Logic").GetString());
    }

    [Fact]
    public async Task Cp949Ascii_KoreanAdded_WrittenAsCp949()
    {
        Prepare();
        const string korean = "  GetTotal = qty * price ' 합계";
        _backend.Enqueue(Block(Body, korean));

        var exit = await Run();

        Assert.Equal(0, exit);
        Assert.Equal(Encoding.GetEncoding(949).GetBytes(Page.Replace(Body, korean)), _fs.File.ReadAllBytes(PagePath));
    }

    [Fact]
    public async Task LinesTarget_LineCountChanged_ThenRetry_SliceFollowsShiftedSpan()
    {
        Prepare();
        // Lines 8–9: attempt 1 adds a line (an undeclared name, so VERIFY fails after the write); attempt 2 must be able
        // to edit the shifted range 8–10, which only works when SLICE rebuilds its regions from CurrentSpan.
        const string first = "total = GetTotal(2, 3)\nextra = 1\ncmd.CommandText = \"dbo.usp_GetOrder\"";
        const string second = "total = GetTotal(2, 3)\ntotal = total + 1\ncmd.CommandText = \"dbo.usp_GetOrder\"";
        _backend.Enqueue(Block("total = GetTotal(2, 3)\ncmd.CommandText = \"dbo.usp_GetOrder\"", first));
        _backend.Enqueue("Declare it or drop it.</think>", evalCount: 10);
        _backend.Enqueue(Block(first, second));

        var exit = await Run(proc: null, lines: new TextSpanInfo(8, 9));

        Assert.Equal(0, exit);
        Assert.Contains("extra", _backend.Requests[1].RawPrompt);
        Assert.Equal(Page.Replace("total = GetTotal(2, 3)\ncmd", "total = GetTotal(2, 3)\ntotal = total + 1\ncmd"),
            _fs.File.ReadAllText(PagePath));
        Assert.Equal("Lines", Summary().GetProperty("asp").GetProperty("unit").GetString());
    }
}
