using System.Xml;
using System.Xml.Linq;

namespace PellyCoder.Core.Testing;

/// <summary>
/// One test outcome; <see cref="Message"/> is the first line of the failure message, <see cref="Detail"/> the whole message
/// (<c>\r\n</c> as <c>\n</c>) and <see cref="StackTrace"/> the whole stack trace.
/// </summary>
public sealed record TestCaseResult(string FullName, bool Passed, string? Message, string? Detail = null, string? StackTrace = null);

/// <summary>A parsed TRX: executed results, whether the run was cut short, and the names of tests that were not executed (skipped).</summary>
public sealed record TrxReport(IReadOnlyList<TestCaseResult> Results, bool RunAborted, IReadOnlyList<string> Skipped);

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
        var full = ParseFull(trxXml);
        return (full.Results, full.RunAborted);
    }

    /// <summary>Like <see cref="ParseReport"/> plus the skipped (<c>NotExecuted</c>) test names.</summary>
    public static TrxReport ParseFull(string trxXml)
    {
        var doc = Load(trxXml);
        var outcome = (string?)doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResultSummary")?.Attribute("outcome");
        var (results, skipped) = Read(doc);
        return new TrxReport(results, outcome is not null && AbnormalOutcomes.Contains(outcome), skipped);
    }

    private static XDocument Load(string trxXml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(trxXml), settings);
        return XDocument.Load(reader);
    }

    private static List<TestCaseResult> Results(XDocument doc) => Read(doc).Results;

    private static (List<TestCaseResult> Results, List<string> Skipped) Read(XDocument doc)
    {
        // testId (or executionId) -> className of the TestMethod; MSTest writes short testNames without the class.
        var classes = new Dictionary<string, string>();
        foreach (var unit in doc.Descendants().Where(e => e.Name.LocalName == "UnitTest"))
        {
            var cls = (string?)unit.Descendants().FirstOrDefault(m => m.Name.LocalName == "TestMethod")?.Attribute("className");
            if (string.IsNullOrEmpty(cls)) continue;
            var id = (string?)unit.Attribute("id");
            if (id is not null) classes.TryAdd(id, cls);
            var exec = (string?)unit.Descendants().FirstOrDefault(m => m.Name.LocalName == "Execution")?.Attribute("id");
            if (exec is not null) classes.TryAdd(exec, cls);
        }

        var results = new List<TestCaseResult>();
        var skipped = new List<string>();
        // Only top-level results: a data-driven test's InnerResults children are not tests of their own.
        foreach (var e in doc.Descendants()
                     .Where(e => e.Name.LocalName == "UnitTestResult" && e.Parent?.Name.LocalName == "Results"))
        {
            var outcome = (string?)e.Attribute("outcome");
            var name = (string?)e.Attribute("testName");
            if (name is null || outcome is null) continue;

            if (!name.Contains('.'))
            {
                var key = (string?)e.Attribute("testId") ?? (string?)e.Attribute("executionId");
                if (key is not null && classes.TryGetValue(key, out var cls)) name = cls + "." + name;
            }
            if (outcome == "NotExecuted") { skipped.Add(name); continue; }

            var passed = outcome == "Passed";
            string? message = null, detail = null, stack = null;
            if (!passed)
            {
                var error = e.Descendants().FirstOrDefault(m => m.Name.LocalName == "ErrorInfo");
                var text = (error ?? e).Descendants().FirstOrDefault(m => m.Name.LocalName == "Message")?.Value;
                message = text?.Split('\n', 2)[0].TrimEnd('\r');
                detail = text?.Replace("\r\n", "\n");
                stack = error?.Elements().FirstOrDefault(m => m.Name.LocalName == "StackTrace")?.Value;
            }
            results.Add(new TestCaseResult(name, passed, message, detail, stack));
        }
        return (results, skipped);
    }
}
