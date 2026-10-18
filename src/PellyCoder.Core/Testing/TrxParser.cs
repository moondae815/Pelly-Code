using System.Xml.Linq;

namespace PellyCoder.Core.Testing;

/// <summary>One test outcome; <see cref="Message"/> is the first line of the failure message.</summary>
public sealed record TestCaseResult(string FullName, bool Passed, string? Message);

/// <summary>Reads the per-test results of a Visual Studio TRX file (namespace-agnostic).</summary>
public static class TrxParser
{
    public static IReadOnlyList<TestCaseResult> Parse(string trxXml)
    {
        var results = new List<TestCaseResult>();
        foreach (var e in XDocument.Parse(trxXml).Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            var outcome = (string?)e.Attribute("outcome");
            var name = (string?)e.Attribute("testName");
            if (name is null || outcome is null || outcome == "NotExecuted") continue;

            var passed = outcome == "Passed";
            string? message = null;
            if (!passed)
            {
                var text = e.Descendants().FirstOrDefault(m => m.Name.LocalName == "Message")?.Value;
                message = text?.Split('\n', 2)[0].TrimEnd('\r');
            }
            results.Add(new TestCaseResult(name, passed, message));
        }
        return results;
    }
}
