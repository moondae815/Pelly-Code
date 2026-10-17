using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Llm;

public sealed class ModelMemberPickerTests
{
    private static readonly MemberCandidate Cancel = new("OrderService.CancelAsync(int, string)", "OrderService", new TextSpanInfo(30, 96));
    private static readonly MemberCandidate Refund = new("OrderService.CalculateRefundAsync(int)", "OrderService", new TextSpanInfo(98, 216));
    private static readonly MemberCandidate M1 = new("C.M(int)", "C", new TextSpanInfo(3, 5));
    private static readonly MemberCandidate M2 = new("C.M(string)", "C", new TextSpanInfo(6, 8));

    private readonly ScriptedLlmBackend _backend = new();
    private readonly PellyOptions _options = new();

    private ModelMemberPicker Picker() =>
        new(_backend, QwenChatTemplate.LoadDefault(), PromptResources.LoadDefault(), _options.Llm, _options.Sampling);

    private Task<MemberCandidate?> Pick(string answer, params MemberCandidate[] members)
    {
        _backend.Enqueue(answer);
        return Picker().PickAsync(members, "환불 금액을 바꿔줘", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendsExactPrompt_NumPredict32_EmptyThink()
    {
        await Pick("OrderService.CancelAsync(int, string)", Cancel, Refund);

        var req = Assert.Single(_backend.Requests);
        Assert.Equal(
            "<|im_start|>system\n" +
            "You choose which C# member a change request is about.\n" +
            "Answer with exactly one line copied from the MEMBERS list, nothing else.<|im_end|>\n" +
            "<|im_start|>user\n" +
            "### MEMBERS\n" +
            "- OrderService.CancelAsync(int, string)\n" +
            "- OrderService.CalculateRefundAsync(int)\n" +
            "### REQUEST\n" +
            "환불 금액을 바꿔줘<|im_end|>\n" +
            "<|im_start|>assistant\n" +
            "<think>\n\n</think>\n\n",
            req.RawPrompt);
        Assert.Equal(32, req.NumPredict);
        Assert.Equal(_options.Sampling.Code, req.Sampling);
        Assert.Equal(["<|im_end|>"], req.Stop);
        Assert.Equal(_options.Llm.NumCtx, req.NumCtx);
        Assert.Equal(_options.Llm.KeepAlive, req.KeepAlive);
    }

    [Fact]
    public async Task ExactDisplay_IsPicked()
    {
        Assert.Same(Refund, await Pick("OrderService.CalculateRefundAsync(int)", Cancel, Refund));
        Assert.Same(M2, await Pick("C.M(string)", M1, M2));
    }

    [Fact]
    public async Task NameOnly_UniqueMatch_IsPicked()
    {
        Assert.Same(Refund, await Pick("CalculateRefundAsync", Cancel, Refund));
        Assert.Same(Cancel, await Pick("OrderService.CancelAsync(int)", Cancel, Refund)); // wrong parameters, unique name
    }

    [Fact]
    public async Task NameOnly_Overloads_IsNull()
    {
        Assert.Null(await Pick("M", M1, M2));
    }

    [Fact]
    public async Task UnknownAnswer_IsNull()
    {
        Assert.Null(await Pick("DeleteAsync", Cancel, Refund));
        Assert.Null(await Pick("", Cancel, Refund));
        Assert.Null(await Pick("\n\n<|im_end|>OrderService.CancelAsync(int, string)", Cancel, Refund));
    }

    [Fact]
    public async Task DashPrefix_IsStripped()
    {
        Assert.Same(Cancel, await Pick("\n  - OrderService.CancelAsync(int, string)  \nsecond line", Cancel, Refund));
    }

    [Fact]
    public async Task TextAfterEndOfTurn_IsIgnored()
    {
        // The engine ignores stop (D23): anything after the end-of-turn token is not part of the answer.
        Assert.Same(Refund, await Pick("OrderService.CalculateRefundAsync(int)<|im_end|>\n- OrderService.CancelAsync(int, string)", Cancel, Refund));
    }
}
