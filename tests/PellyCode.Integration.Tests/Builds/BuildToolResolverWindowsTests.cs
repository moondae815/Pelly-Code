using System.IO.Abstractions;
using PellyCode.Core.Builds;
using PellyCode.Core.Config;
using PellyCode.Core.Processes;
using PellyCode.Integration.Tests.Infrastructure;

namespace PellyCode.Integration.Tests.Builds;

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
