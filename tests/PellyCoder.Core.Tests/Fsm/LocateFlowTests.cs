using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>Target discovery in LOCATE (design 8.4): --line, identifiers in the request, then a model pick.</summary>
public sealed class LocateFlowTests : IDisposable
{
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(60);

    // Lines: 3 class, 5 field, 7-10 Divide, 11 blank, 12-15 Add.
    private const string CalcSource =
        "namespace Demo;\n" +
        "\n" +
        "public class Calc\n" +
        "{\n" +
        "    private int _count;\n" +
        "\n" +
        "    public int Divide(int a, int b)\n" +
        "    {\n" +
        "        return a / b;\n" +
        "    }\n" +
        "\n" +
        "    public int Add(int a, int b)\n" +
        "    {\n" +
        "        return a + b;\n" +
        "    }\n" +
        "}\n";

    private const string Original = "        return a / b;";
    private const string Guarded = "        if (b == 0) throw new DivideByZeroException();\n        return a / b;";
    private const string NoIdentifiers = "0으로 나누면 예외를 던지게 해줘";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-locate-flow-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly PellyOptions _options = new();

    public LocateFlowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        File.WriteAllText(Path.Combine(_root, "Demo", "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(CalcPath, CalcSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");

    private RunRequest Req(string request, int? line = null, string? method = null, string? className = null) =>
        new(CalcPath, className, method, line, request, true, false, null);

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private Task<int> Run(RunRequest request) =>
        FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes, null)
            .RunAsync(request, TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    private static ProcessResult Ok() => new(0, "", "", Encoding.UTF8.GetBytes(""), TimeSpan.FromSeconds(1), false);

    private JsonElement Summary()
    {
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories));
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement;
    }

    /// <summary>The run never got past LOCATE: no model call, no baseline build, file bytes untouched.</summary>
    private void AssertStoppedInLocate(byte[] before)
    {
        Assert.Empty(_backend.Requests);
        Assert.Empty(_processes.Calls);
        Assert.Equal(before, File.ReadAllBytes(CalcPath));
    }

    [Fact]
    public async Task Line_OnField_IsNotFound_WithCursorHint()
    {
        var before = File.ReadAllBytes(CalcPath);

        var exit = await Run(Req("널 체크 추가", line: 5));

        Assert.Equal(3, exit);
        Assert.Contains("5번 줄은 메서드 안이 아닙니다. 메서드 안에 커서를 두고 실행하세요", _ui.Warnings);
        AssertStoppedInLocate(before);
        Assert.Equal("TargetNotFound", Summary().GetProperty("failureKind").GetString());
    }

    [Theory]
    [InlineData(3)]  // class declaration
    [InlineData(11)] // blank line between members
    public async Task Line_OutsideMembers_IsNotFound(int line)
    {
        var before = File.ReadAllBytes(CalcPath);

        var exit = await Run(Req("널 체크 추가", line: line));

        Assert.Equal(3, exit);
        Assert.Contains($"{line}번 줄은 메서드 안이 아닙니다. 메서드 안에 커서를 두고 실행하세요", _ui.Warnings);
        AssertStoppedInLocate(before);
    }

    [Fact]
    public async Task Line_InsideMethod_EditsThatMember_WithoutModelPick()
    {
        _processes.Enqueue(Ok()); // baseline
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(NoIdentifiers, line: 9));

        Assert.Equal(0, exit);
        Assert.Single(_backend.Requests);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task Line_InsideOperator_EditsThatOperator()
    {
        const string src =
            "namespace Demo;\n\npublic readonly struct Money\n{\n    public decimal Value { get; init; }\n\n" +
            "    public static Money operator +(Money a, Money b)\n    {\n        return new Money { Value = a.Value + b.Value };\n    }\n}\n";
        const string body = "        return new Money { Value = a.Value + b.Value };";
        const string rounded = "        return new Money { Value = Math.Round(a.Value + b.Value, 2) };";
        File.WriteAllText(CalcPath, src);
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(body, rounded));

        var exit = await Run(Req("합계를 소수 둘째 자리로 반올림", line: 9));

        Assert.True(exit == 0, string.Join(" | ", _ui.Warnings));
        Assert.Contains("[EDITABLE: target]\n    public static Money operator +(Money a, Money b)", Assert.Single(_backend.Requests).RawPrompt);
        Assert.Equal(src.Replace(body, rounded), File.ReadAllText(CalcPath));
    }

    [Theory]
    [InlineData("Divide", null)]
    [InlineData(null, "Calc")]
    public async Task Line_WithMethod_IsEnvError(string? method, string? className)
    {
        var before = File.ReadAllBytes(CalcPath);

        var exit = await Run(Req("널 체크 추가", line: 9, method: method, className: className));

        Assert.Equal(4, exit);
        Assert.Contains("--line은 --class/--method와 함께 쓸 수 없습니다", _ui.Warnings);
        AssertStoppedInLocate(before);
    }

    [Fact]
    public async Task RequestNamesMethod_IsFoundWithoutModelCall()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req("`Divide`를 고쳐줘"));

        Assert.Equal(0, exit);
        var edit = Assert.Single(_backend.Requests); // EDIT only
        Assert.NotEqual(32, edit.NumPredict);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task RequestNamesTwoMembers_NonInteractive_IsAmbiguous()
    {
        var before = File.ReadAllBytes(CalcPath);

        var exit = await Run(Req("`Divide`와 `Add`를 고쳐줘"));

        Assert.Equal(3, exit);
        AssertStoppedInLocate(before);
        Assert.Equal("TargetAmbiguous", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task NoIdentifiers_ModelPicks_Exit0()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue("Calc.Divide(int, int)"); // the pick
        _backend.Enqueue(Block(Original, Guarded)); // the patch

        var exit = await Run(Req(NoIdentifiers));

        Assert.Equal(0, exit);
        Assert.Equal(2, _backend.Requests.Count);
        Assert.Equal(32, _backend.Requests[0].NumPredict);
        Assert.Contains("- Calc.Divide(int, int)\n- Calc.Add(int, int)\n", _backend.Requests[0].RawPrompt);
        Assert.Contains("대상: Calc.Divide(int, int) (모델이 선택)", _ui.Warnings);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task ModelAnswerNotInList_Exit3()
    {
        var before = File.ReadAllBytes(CalcPath);
        _backend.Enqueue("Calc.Multiply(int, int)");

        var exit = await Run(Req(NoIdentifiers));

        Assert.Equal(3, exit);
        Assert.Contains("요청문에서 대상 멤버를 찾지 못했습니다. --method나 --line을 쓰세요", _ui.Warnings);
        Assert.Single(_backend.Requests);
        Assert.Empty(_processes.Calls);
        Assert.Equal(before, File.ReadAllBytes(CalcPath));
        Assert.Equal("TargetNotFound", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task ModelPick_ServerUnavailable_IsEnvError()
    {
        var before = File.ReadAllBytes(CalcPath);
        _backend.EnqueueException(new LlmUnavailableException("connection refused"));

        var exit = await Run(Req(NoIdentifiers));

        Assert.Equal(4, exit);
        Assert.Contains("LLM 서버를 사용할 수 없습니다: connection refused (`pelly doctor`로 확인하세요)", _ui.Warnings);
        Assert.Empty(_processes.Calls);
        Assert.Equal(before, File.ReadAllBytes(CalcPath));
    }

    [Fact]
    public async Task MoreThan60Members_Exit3_WithHint()
    {
        File.WriteAllText(CalcPath, ManyMembers(61));
        var before = File.ReadAllBytes(CalcPath);

        var exit = await Run(Req(NoIdentifiers));

        Assert.Equal(3, exit);
        Assert.Contains("멤버가 60개를 넘어 자동으로 고를 수 없습니다. --method나 --line을 쓰세요", _ui.Warnings);
        AssertStoppedInLocate(before);
    }

    [Fact]
    public async Task Exactly60Members_AsksTheModel()
    {
        File.WriteAllText(CalcPath, ManyMembers(60));
        _backend.Enqueue("nothing");

        var exit = await Run(Req(NoIdentifiers));

        Assert.Equal(3, exit);
        var pick = Assert.Single(_backend.Requests);
        Assert.Contains("- Calc.M60()\n### REQUEST\n", pick.RawPrompt);
    }

    private static string ManyMembers(int count) =>
        "namespace Demo;\n\npublic class Calc\n{\n"
        + string.Concat(Enumerable.Range(1, count).Select(i => $"    public void M{i}() {{ }}\n"))
        + "}\n";
}
