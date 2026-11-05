using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Integration.Tests.Asp;
using PellyCoder.Integration.Tests.Infrastructure;
using PellyCoder.Integration.Tests.Web;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// Real-model run of <c>pelly js</c> on a copy of eval/corpus/Asp (local only: needs Ollama). Think mode stays at its default.
/// The model may fail the request (exit 1), but pelly must never touch bytes outside the target function, and an accepted edit
/// must have passed the syntax, level and safety checks.
/// </summary>
public sealed class WebEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly AspCorpusCopy _copy = AspCorpusCopy.Create();

    public void Dispose() => _copy.Dispose();

    [OllamaFact]
    public async Task RealModel_ListJs_StatusParam()
    {
        var file = _copy.File("js/order/list.js");
        var before = File.ReadAllBytes(file);
        var span = WebE2E.FunctionSpan(before, AspE2E.Cp949, "function fnSearch()");
        var options = AspE2E.LoadOptions(_copy.Root, new Dictionary<string, string?>
        {
            ["llm:endpoint"] = TestEnv.OllamaEndpoint,
            ["llm:model"] = TestEnv.OllamaModel,
        });

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var backend = new OllamaBackend(http, options.Llm, TimeProvider.System);
        var request = new RunRequest(file, null, "fnSearch", null,
            "검색할 때 #status 값도 status라는 이름으로 서버에 같이 보내줘",
            Yes: true, DryRun: false, MaxRetries: null, Kind: EditKind.Js);

        var run = await WebE2E.RunAsync(_copy.Root, options, backend, request, output, nameof(RealModel_ListJs_StatusParam));

        Assert.True(run.Exit is ExitCodes.Success or ExitCodes.VerifyFailed, $"exit {run.Exit}");
        var after = File.ReadAllBytes(file);
        if (run.Exit != ExitCodes.Success)
        {
            Assert.Equal(before, after);
            return;
        }
        output.WriteLine(AspE2E.Cp949.GetString(AspE2E.AssertOutsideBytesIdentical(before, after, span)));
        var checks = run.Web.GetProperty("checks");
        Assert.Equal("passed", checks.GetProperty("Syntax").GetString());
        Assert.Equal("passed", checks.GetProperty("Level").GetString());
        Assert.Equal("passed", checks.GetProperty("Safety").GetString());
    }
}
