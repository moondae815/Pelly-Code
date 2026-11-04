using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.Llm;

public sealed class PromptResourcesTests
{
    private static readonly PromptResources Resources = PromptResources.LoadDefault();

    [Fact]
    public void Resources_AspVersion_AspV1()
    {
        Assert.Equal("asp-v1", Resources.AspVersion);
        Assert.Equal("asp-v1", Resources.VersionFor(EditKind.Asp));
        Assert.Equal("sql-v1", Resources.VersionFor(EditKind.Sql));
        Assert.DoesNotContain("# version", Resources.SystemAsp);
        Assert.DoesNotContain("# version", Resources.FewShotAsp);
        Assert.Contains("{LanguageHint}", Resources.SystemAsp);
        Assert.Contains("<<<<<<< SEARCH", Resources.SystemAsp);
        Assert.Contains("Replace(value, \"'\", \"''\")", Resources.SystemAsp);
        Assert.Contains("VBScript built-in functions and the site SQL functions named in RULES", Resources.SystemAsp);
        Assert.Contains("Methods of ADO objects such as conn.Execute or cmd.Execute are fine.", Resources.SystemAsp);
    }
}
