using PellyCoder.Integration.Tests.Corpus;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// Offline checks of the R7 corruption used by <see cref="BuildEndToEndTests"/>: <c>undefinedVar++;</c> must land as a
/// statement inside CancelAsync's body, before the last REPLACE line when that is inside the body, else before the
/// method's last <c>}</c>. Responses start after the prefilled <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; SEARCH</c> line, like real ones.
/// </summary>
public sealed class BuildErrorInjectionTests
{
    private static readonly string Original =
        File.ReadAllText(Path.Combine(CorpusPaths.Get("Modern"), "Shop.Api", "Services", "OrderService.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void LastReplaceLineInsideBody_InsertsBeforeIt()
    {
        const string response = """
                        if (order.Status == OrderStatus.Pending)
            =======
                        if (order.Status == OrderStatus.Shipped)
                        {
                            throw new InvalidOperationException("x");
                        }

                        if (order.Status == OrderStatus.Pending)
            >>>>>>> REPLACE
            """;

        var injected = BuildEndToEndTests.InjectUndefinedVar(response, Original);

        Assert.Contains("            undefinedVar++;\n            if (order.Status == OrderStatus.Pending)\n>>>>>>> REPLACE", injected, StringComparison.Ordinal);
        Assert.True(BuildEndToEndTests.InjectedInsideCancelAsyncBody(Original, injected));
    }

    [Fact]
    public void LastReplaceLineEndsAnExpression_FallsBackToBeforeMethodClosingBrace()
    {
        // Inserting before "refund);" would split the LogInformation call: not a statement in the body.
        const string response = """
                            order.Status,
                            refund);
            =======
                            order.Status,
                            refund);
            >>>>>>> REPLACE
            """;

        var injected = BuildEndToEndTests.InjectUndefinedVar(response, Original);

        Assert.True(BuildEndToEndTests.InjectedInsideCancelAsyncBody(Original, injected));
        Assert.Contains("refund);\n            undefinedVar++;\n        }", BuildEndToEndTests.Patch(Original, injected), StringComparison.Ordinal);
    }

    [Fact]
    public void LastReplaceLineInAddedHelper_FallsBackToBeforeMethodClosingBrace()
    {
        const string response = """
                            refund);
                    }
            =======
                            refund);
                    }

                    private static void Helper()
                    {
                    }
            >>>>>>> REPLACE
            """;

        var injected = BuildEndToEndTests.InjectUndefinedVar(response, Original);

        Assert.True(BuildEndToEndTests.InjectedInsideCancelAsyncBody(Original, injected));
        var patched = BuildEndToEndTests.Patch(Original, injected);
        Assert.Contains("refund);\n            undefinedVar++;\n        }\n\n        private static void Helper()\n        {\n        }", patched, StringComparison.Ordinal);
    }
}
