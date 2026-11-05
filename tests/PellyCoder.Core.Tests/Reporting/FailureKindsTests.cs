using System.Text.RegularExpressions;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Tests.Reporting;

public sealed partial class FailureKindsTests
{
    [GeneratedRegex("FailureKind\\s*=\\s*\"(?<v>[^\"]+)\"")]
    private static partial Regex Assigned();

    [GeneratedRegex("PhaseOutcome\\.\\w+\\s*=>\\s*\"(?<v>[^\"]+)\"")]
    private static partial Regex Label();

    [GeneratedRegex("string\\s+Name\\s*=>\\s*\"(?<v>[^\"]+)\"")]
    private static partial Regex StepName();

    [GeneratedRegex("StepResult\\.Fail\\(")]
    private static partial Regex StepFail();

    // A Fail call whose last argument is a FailureKinds constant: VERIFY then never uses the {Name}Errors default.
    [GeneratedRegex("StepResult\\.Fail\\([^;]*,\\s*FailureKinds\\.\\w+\\)+\\s*;")]
    private static partial Regex StepFailWithKind();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PellyCoder.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("PellyCoder.sln not found above the test binaries.");
    }

    [Theory]
    [InlineData("TestGreenFailed")]
    [InlineData("TestRegression")]
    [InlineData("TestNotFound")]
    [InlineData("TestAmbiguous")]
    [InlineData("TestUnverifiable")]
    [InlineData("TestRedDeclined")]
    [InlineData("TestFileChanged")]
    [InlineData("GroupTestGreenFailed")]
    [InlineData("GroupTestRegression")]
    [InlineData("GroupTestUnverifiable")]
    [InlineData("GroupTestFileChanged")]
    public void FailureKinds_IncludesTestFirstKinds(string kind) => Assert.True(FailureKinds.IsKnown(kind));

    [Fact]
    public void EveryFailureKindLiteralInSource_IsKnown()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in Assigned().Matches(text)) found.Add(m.Groups["v"].Value);
            if (file.EndsWith("FsmOrchestrator.cs", StringComparison.Ordinal))
                foreach (Match m in Label().Matches(text)) found.Add(m.Groups["v"].Value);
            var fails = StepFail().Count(text);
            // Every failure names its own kind, or the step never fails at all (warning-only steps): `{Name}Errors` is never used.
            var labelUnused = fails == StepFailWithKind().Count(text) && !text.Contains("StepResult.Environment(", StringComparison.Ordinal);
            if (text.Contains("IVerifierStep", StringComparison.Ordinal) && !labelUnused)
                foreach (Match m in StepName().Matches(text)) found.Add(m.Groups["v"].Value + "Errors");
        }

        Assert.NotEmpty(found);
        Assert.Contains("TargetNotFound", found);   // the scan really reads the phases
        Assert.Contains("BuildErrors", found);      // and the verifier step names
        Assert.Contains("SyntaxErrors", found);     // and the orchestrator labels
        Assert.All(found, k => Assert.True(FailureKinds.IsKnown(k), $"unknown FailureKind: {k}"));
    }

    [Fact]
    public void IsKnown_RejectsFreeText()
    {
        Assert.False(FailureKinds.IsKnown("C:\\x\\a.cs"));
        Assert.False(FailureKinds.IsKnown(""));
        Assert.False(FailureKinds.IsKnown(null));
        Assert.True(FailureKinds.IsKnown(FailureKinds.TestErrors));
    }
}
