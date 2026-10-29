using PellyCoder.Core.CharTests;
using PellyCoder.Core.Models;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class CharTestRequestTests
{
    [Fact]
    public void Limits_AreOneToFiveDefaultThree()
    {
        Assert.Equal(1, CharTestLimits.MinCount);
        Assert.Equal(5, CharTestLimits.MaxCount);
        Assert.Equal(3, CharTestLimits.DefaultCount);
    }

    [Fact]
    public void Request_CarriesFields()
    {
        var r = new CharTestRequest("a.cs", "C", "M", 7, "hint", 3, null, false, true, null, false);
        Assert.Equal("a.cs", r.FilePath);
        Assert.Equal(3, r.Count);
        Assert.True(r.DryRun);
    }

    [Fact]
    public void FailureKinds_CharTestKindsAreKnown()
    {
        string[] kinds =
        [
            FailureKinds.CharTestShape, FailureKinds.CharTestNoAssert, FailureKinds.CharTestEnvDependent,
            FailureKinds.CharTestBuild, FailureKinds.CharTestFailsOnCurrent, FailureKinds.CharTestRegression,
            FailureKinds.CharTestFlaky, FailureKinds.CharTestSurvivesMutation, FailureKinds.CharTestMutationUnbuildable,
            FailureKinds.CharTestBaselineUnusable, FailureKinds.CharTestPending,
        ];
        Assert.Equal(11, kinds.Distinct().Count());
        foreach (var k in kinds)
        {
            Assert.True(FailureKinds.IsKnown(k));
            Assert.StartsWith("CharTest", k);
        }
    }

    [Fact]
    public void PromptVersion_ForCSharpTest()
    {
        Assert.Equal("cs-test-v1", PromptResources.LoadDefault().VersionFor(EditKind.CSharpTest));
    }
}
