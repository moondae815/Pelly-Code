using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Integration.Tests.Asp;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// Real-model run of <c>pelly asp</c> on a copy of eval/corpus/Asp (local only: needs Ollama). Think mode stays at its
/// default. The model may fail the request (exit 1), but pelly must never touch bytes outside the target lines, and an
/// accepted edit must have passed the safety and structure checks.
/// </summary>
public sealed class AspEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly AspCorpusCopy _copy = AspCorpusCopy.Create();

    public void Dispose() => _copy.Dispose();

    [OllamaFact]
    public async Task RealModel_ListPage_StatusFilter()
    {
        var file = _copy.File("admin/order/list.asp");
        var before = File.ReadAllBytes(file);
        var lines = AspE2E.SpanBetween(before, AspE2E.Cp949, "' 검색 조건", "' 검색 조건 끝");
        var options = AspE2E.LoadOptions(_copy.Root, new Dictionary<string, string?>
        {
            ["llm:endpoint"] = TestEnv.OllamaEndpoint,
            ["llm:model"] = TestEnv.OllamaModel,
        });

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var backend = new OllamaBackend(http, options.Llm, TimeProvider.System);
        var request = new RunRequest(file, null, null, null,
            "주문 상태(status) 값이 있으면 usp_SearchOrders의 @Status 검색 조건으로 넘겨줘",
            Yes: true, DryRun: false, MaxRetries: null, Kind: EditKind.Asp, Lines: lines);

        var run = await AspE2E.RunAsync(_copy.Root, options, backend, request, output, nameof(RealModel_ListPage_StatusFilter));

        Assert.True(run.Exit is ExitCodes.Success or ExitCodes.VerifyFailed, $"exit {run.Exit}");
        var after = File.ReadAllBytes(file);
        if (run.Exit != ExitCodes.Success)
        {
            Assert.Equal(before, after);
            return;
        }
        var changed = AspE2E.AssertOutsideBytesIdentical(before, after, lines);
        output.WriteLine(AspE2E.Cp949.GetString(changed));
        Assert.Contains("@Status", AspE2E.Cp949.GetString(changed), StringComparison.Ordinal);
        var checks = run.Asp.GetProperty("checks");
        Assert.Equal("passed", checks.GetProperty("Safety").GetString());
        Assert.Equal("passed", checks.GetProperty("Structure").GetString());
    }
}
