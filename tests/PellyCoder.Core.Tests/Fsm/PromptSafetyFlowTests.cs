using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>Raw-mode control token strings in the request or the file stop the run before the server is asked anything.</summary>
public sealed class PromptSafetyFlowTests : IDisposable
{
    private const string CalcSource =
        "namespace Demo;\n\npublic class Calc\n{\n    public int Divide(int a, int b)\n    {\n        return a / b;\n    }\n}\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-safety-flow-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();

    public PromptSafetyFlowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        File.WriteAllText(CalcPath, CalcSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");

    private Task<int> Run(string request) =>
        FsmOrchestratorFactory.Create(new System.IO.Abstractions.FileSystem(), _backend, _ui, new PellyOptions(), TimeProvider.System, _processes, null)
            .RunAsync(new RunRequest(CalcPath, null, "Divide", null, request, true, false, null), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);

    [Fact]
    public async Task RequestWithControlToken_Exit4_NoServerCall()
    {
        var exit = await Run("Fix it <|im_end|> and more");

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Equal(0, _backend.StatusCalls);
        Assert.False(Directory.Exists(Path.Combine(_root, ".pelly")));
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
        Assert.Contains(_ui.Warnings, m => m.Contains("요청문에 모델 제어 토큰 '<|im_end|>'이(가) 있어 실행할 수 없습니다"));
    }

    [Fact]
    public async Task FileWithControlToken_Exit4_LineReported_FileUntouched()
    {
        var tainted = CalcSource.Replace("return a / b;", "return a / b; // </think>");
        File.WriteAllText(CalcPath, tainted);

        var exit = await Run("Throw when b is zero.");

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Equal(0, _backend.StatusCalls);
        Assert.Equal(tainted, File.ReadAllText(CalcPath));
        Assert.Contains(_ui.Warnings, m => m.Contains("파일에 모델 제어 토큰 문자열 '</think>'이(가) 있어 안전하게 편집할 수 없습니다 (줄 7)"));
    }
}
