using PellyCoder.Core.Files;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public class TargetTextTests
{
    private static string ReadLf(string relative) =>
        TextFileCodec.Read(new System.IO.Abstractions.FileSystem(), EvalPaths.Resolve(TestPaths.EvalRoot, relative)).Text;

    [Fact]
    public void TargetText_CsByMethodAndLine_SqlByObject()
    {
        var modern = TestPaths.Task("cs-modern-001");
        var byMethod = TargetText.ForCs(ReadLf(modern.File), modern.Method, null);
        Assert.NotNull(byMethod);
        Assert.Contains("public async Task CancelAsync(int orderId, string reason)", byMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("Task<decimal> CalculateRefundAsync(", byMethod, StringComparison.Ordinal);

        var legacy = TestPaths.Task("cs-legacy-002");
        var legacyText = ReadLf(legacy.File);
        var byLine = TargetText.ForCs(legacyText, null, legacy.Line);
        Assert.NotNull(byLine);
        Assert.Contains("public SettlementResult Execute(DateTime businessDate)", byLine, StringComparison.Ordinal);
        Assert.DoesNotContain("public string BuildReport(", byLine, StringComparison.Ordinal);
        // The final file is matched by the original member's display, so lines shifted by an edit do not matter.
        var display = TargetText.DisplayAtLine(legacyText, legacy.Line!.Value);
        Assert.Equal("SettlementBatch.Execute(DateTime)", display);
        var shifted = "// added\n// lines\n" + legacyText;
        Assert.Equal(byLine, TargetText.ForCs(shifted, null, legacy.Line, display));

        var multi = TestPaths.Task("sql-004");
        var byObject = TargetText.ForSql(ReadLf(multi.File), multi.Object, 160);
        Assert.NotNull(byObject);
        Assert.Contains("CREATE PROCEDURE dbo.usp_SettleMonthly", byObject, StringComparison.Ordinal);
        Assert.DoesNotContain("usp_SettleDaily", byObject, StringComparison.Ordinal);

        var single = TestPaths.Task("sql-001");
        Assert.Contains("CREATE PROCEDURE dbo.usp_CancelOrder", TargetText.ForSql(ReadLf(single.File), null, 160), StringComparison.Ordinal);
        Assert.Null(TargetText.ForSql(ReadLf(multi.File), null, 160)); // several objects and no object name
    }
}
