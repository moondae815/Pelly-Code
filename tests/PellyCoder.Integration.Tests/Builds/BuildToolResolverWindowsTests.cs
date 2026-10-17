using System.IO.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Builds;

public sealed class BuildToolResolverWindowsTests
{
    [WindowsFact]
    public async Task RealVswhere_FindsMsBuild()
    {
        var resolver = new BuildToolResolver(new FileSystem(), new SystemProcessRunner(TimeProvider.System), new BuildOptions());
        var r = await resolver.ResolveAsync(ProjectStyle.Legacy, TestContext.Current.CancellationToken);

        Assert.Null(r.Error);
        Assert.NotNull(r.Tool);
        Assert.Equal(BuildToolKind.MsBuild, r.Tool.Kind);
        Assert.True(File.Exists(r.Tool.Executable), r.Tool.Executable);
    }
}
