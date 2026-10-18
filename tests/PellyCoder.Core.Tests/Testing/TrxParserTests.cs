using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.Testing;

public class TrxParserTests
{
    internal static string Fixture(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "tests", "fixtures", "tests", name);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void Trx_ParsesPassFailAndMessages()
    {
        var pass = TrxParser.Parse(Fixture("modern-pass.trx"));
        Assert.Equal(3, pass.Count);
        Assert.All(pass, r => { Assert.True(r.Passed); Assert.Null(r.Message); });
        Assert.Contains(pass, r => r.FullName == "Shop.Api.Tests.OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels");

        var fail = TrxParser.Parse(Fixture("modern-fail.trx"));
        Assert.Equal(3, fail.Count);
        var bad = Assert.Single(fail, r => !r.Passed);
        Assert.Equal("Shop.Api.Tests.OrderServiceTests.CalculateRefundAsync_ShippedOrder_DeductsReturnShippingFee", bad.FullName);
        Assert.Equal("Assert.Equal() Failure: Values differ", bad.Message); // first line only
        Assert.Equal(2, fail.Count(r => r.Passed));
    }

    [Fact]
    public void Trx_ExcludesNotExecuted()
    {
        const string xml = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
              <UnitTestResult testName="A.B" outcome="Passed" />
              <UnitTestResult testName="A.Skipped" outcome="NotExecuted" />
            </Results></TestRun>
            """;
        var r = Assert.Single(TrxParser.Parse(xml));
        Assert.Equal("A.B", r.FullName);
    }

    [Fact]
    public void Trx_IgnoresInnerResults()
    {
        const string xml = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
              <UnitTestResult testName="A.Theory" outcome="Failed"><InnerResults>
                <UnitTestResult testName="A.Theory (1)" outcome="Passed" />
                <UnitTestResult testName="A.Theory (2)" outcome="Failed" />
              </InnerResults></UnitTestResult>
            </Results></TestRun>
            """;
        Assert.Equal("A.Theory", Assert.Single(TrxParser.Parse(xml)).FullName);
    }

    [Fact]
    public void Trx_RejectsDtd()
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() => TrxParser.Parse("<!DOCTYPE x [<!ENTITY e \"v\">]><TestRun/>"));
    }
}
