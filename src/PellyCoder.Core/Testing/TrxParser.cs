using System.Xml;
using System.Xml.Linq;

namespace PellyCoder.Core.Testing;

/// <summary>One test outcome; <see cref="Message"/> is the first line of the failure message.</summary>
public sealed record TestCaseResult(string FullName, bool Passed, string? Message);

/// <summary>Reads the per-test results of a Visual Studio TRX file (namespace-agnostic).</summary>
public static class TrxParser
{
    /// <summary>
    /// Run outcomes (<c>ResultSummary/@outcome</c>) of a run that did not finish normally. A finished run says
    /// <c>Completed</c> or <c>Failed</c>; measured 2026-10-19 (SDK 10, Test.Sdk 17.14.1, xunit 2.9.3): a VSTest host killed by
    /// <c>Environment.FailFast</c> still writes <c>Failed</c> with zero results, so this list alone does not catch that crash.
    /// </summary>
    private static readonly HashSet<string> AbnormalOutcomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Aborted", "Error", "Timeout", "Disconnected", "PassedButRunAborted", "InProgress", "Pending",
    };

    public static IReadOnlyList<TestCaseResult> Parse(string trxXml) => Results(Load(trxXml));

    /// <summary>Per-test results and whether the run outcome says the run was cut short (<see cref="AbnormalOutcomes"/>).</summary>
    public static (IReadOnlyList<TestCaseResult> Results, bool RunAborted) ParseReport(string trxXml)
    {
        var doc = Load(trxXml);
        var outcome = (string?)doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResultSummary")?.Attribute("outcome");
        return (Results(doc), outcome is not null && AbnormalOutcomes.Contains(outcome));
    }

    private static XDocument Load(string trxXml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(trxXml), settings);
        return XDocument.Load(reader);
    }

    private static List<TestCaseResult> Results(XDocument doc)
    {
        var results = new List<TestCaseResult>();
        // Only top-level results: a data-driven test's InnerResults children are not tests of their own.
        foreach (var e in doc.Descendants()
                     .Where(e => e.Name.LocalName == "UnitTestResult" && e.Parent?.Name.LocalName == "Results"))
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
