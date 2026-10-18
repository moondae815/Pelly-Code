using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Config;
using static PellyCoder.Core.Tests.TestPaths;

namespace PellyCoder.Core.Tests.Testing;

public class TestOptionsTests
{
    private static readonly IReadOnlyDictionary<string, string?> None = new Dictionary<string, string?>();

    [Fact]
    public void Options_TestKeysBound_TimeoutValidated()
    {
        var defaults = PellyOptionsLoader.Load(new MockFileSystem(), null, null, None, None).Options!;
        Assert.Null(defaults.Test.Command);
        Assert.Empty(defaults.Test.Projects);
        Assert.Equal(300, defaults.Test.TimeoutSeconds);

        var ws = P(@"C:\w\.pelly\config.json");
        var fs = new MockFileSystem();
        fs.AddFile(ws, new MockFileData("""{"test":{"command":"runner {dll}","projects":["A/A.Tests.csproj","B/B.Tests.csproj"],"timeoutSeconds":45}}"""));
        var bound = PellyOptionsLoader.Load(fs, null, ws, None, None);
        Assert.Empty(bound.Errors);
        Assert.Equal("runner {dll}", bound.Options!.Test.Command);
        Assert.Equal(["A/A.Tests.csproj", "B/B.Tests.csproj"], bound.Options.Test.Projects);
        Assert.Equal(45, bound.Options.Test.TimeoutSeconds);

        var bad = PellyOptionsLoader.Load(new MockFileSystem(), null, null, None,
            new Dictionary<string, string?> { ["test:timeoutSeconds"] = "0" });
        Assert.Contains(bad.Errors, e => e.Contains("test:timeoutSeconds"));
    }
}
