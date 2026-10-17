using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CSharp;

public sealed class IdentifierExtractorTests
{
    [Fact]
    public void Backticks_Quotes_PascalCase_InOrder_Distinct()
    {
        var found = IdentifierExtractor.Candidates("Fix `cancel_order` then \"refund\" in SearchAsync, and `cancel_order` again; Total too");

        Assert.Equal(["Fix", "cancel_order", "refund", "SearchAsync", "Total"], found);
    }

    [Fact]
    public void DottedName_AddsLastSegment()
    {
        Assert.Equal(["OrderService.CancelAsync", "CancelAsync"], IdentifierExtractor.Candidates("`OrderService.CancelAsync`를 고쳐줘"));
        Assert.Equal(["A.B.Run", "Run"], IdentifierExtractor.Candidates("\"A.B.Run\" 수정"));
    }

    [Fact]
    public void PascalCase_FollowedByKoreanParticle_IsFound()
    {
        Assert.Equal(["CancelAsync"], IdentifierExtractor.Candidates("CancelAsync에 널 체크를 추가해줘"));
    }

    [Fact]
    public void KoreanRequest_WithoutIdentifiers_IsEmpty()
    {
        Assert.Empty(IdentifierExtractor.Candidates("주문 취소할 때 사유 길이를 검사하도록 고쳐줘"));
    }
}
