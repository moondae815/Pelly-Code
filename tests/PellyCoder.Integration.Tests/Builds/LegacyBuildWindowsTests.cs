using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Builds;

public sealed class LegacyBuildWindowsTests
{
    // Windows 미확인(실기): CI windows-integration 에서만 실행된다.
    [WindowsFact]
    public async Task LegacyShopWeb_Baseline_Clean_ThenInjectedError_IsNew()
    {
        var ct = TestContext.Current.CancellationToken;
        var packages = CorpusPaths.Get("Legacy/packages");
        Assert.True(Directory.Exists(packages),
            "eval/corpus/Legacy/packages 가 없습니다. 먼저 eval/corpus/Legacy/build.ps1 을 실행하세요 (CI는 Windows tests 앞에서 실행한다).");

        var root = CorpusCopy.Create("Legacy", includeBuildOutput: false);
        try
        {
            var processes = new SystemProcessRunner(TimeProvider.System);
            var resolution = await new BuildToolResolver(new FileSystem(), processes, new BuildOptions())
                .ResolveAsync(ProjectStyle.Legacy, ct);
            Assert.Null(resolution.Error);
            var tool = resolution.Tool!;

            var csproj = Path.Combine(root, "Shop.Web", "Shop.Web.csproj");
            var verifier = new BuildVerifier(processes, new BuildOptions { TimeoutSeconds = 300 });

            var baseline = await verifier.BuildAsync(tool, csproj, root, ct);
            Assert.True(baseline.Errors.Count == 0, string.Join("\n", baseline.Errors));

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp949 = Encoding.GetEncoding(949);
            var file = Path.Combine(root, "Shop.Web", "Services", "OrderService.cs");
            var text = cp949.GetString(await File.ReadAllBytesAsync(file, ct));
            const string anchor = "return _orderDao.GetOrder(orderId);";
            Assert.Contains(anchor, text);
            await File.WriteAllBytesAsync(file, cp949.GetBytes(text.Replace(anchor, "undefinedVar++; " + anchor)), ct);

            var current = await verifier.BuildAsync(tool, csproj, root, ct);

            var e = Assert.Single(BaselineDiff.New(baseline.Errors, current.Errors));
            Assert.Equal("CS0103", e.Code);
            Assert.Equal("Shop.Web/Services/OrderService.cs", e.File);
        }
        finally
        {
            CorpusCopy.Delete(root);
        }
    }
}
