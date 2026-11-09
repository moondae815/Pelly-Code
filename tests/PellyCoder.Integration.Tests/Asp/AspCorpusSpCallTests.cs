using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Config;
using PellyCoder.Core.Web;
using PellyCoder.Integration.Tests.Corpus;

namespace PellyCoder.Integration.Tests.Asp;

/// <summary>The A2 corpus pages call usp_OrderList in three ways; the site index finds all three.</summary>
public sealed class AspCorpusSpCallTests
{
    static AspCorpusSpCallTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The wrapper functions the corpus registers in its own pelly.json (asp.spCallFunctions).</summary>
    private static AspSpCallFunction[] Wrappers(string site)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(site, "pelly.json")));
        if (!doc.RootElement.GetProperty("asp").TryGetProperty("spCallFunctions", out var list))
            return [];
        return [.. list.EnumerateArray().Select(e => new AspSpCallFunction
        {
            Name = e.GetProperty("name").GetString()!, Argument = e.GetProperty("argument").GetInt32(),
        })];
    }

    [Fact]
    public void Corpus_sp_callers_found_for_order_list_sp()
    {
        var site = CorpusPaths.Get("Asp");
        var cache = Path.Combine(Path.GetTempPath(), "pelly-asp-corpus-index-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var index = WebSiteIndex.Load(new FileSystem(), site, new WebFileFilter(site, []), new WebTextCodec("cp949"), cache,
                spCallFunctions: Wrappers(site));

            var calls = index.SpCalls.Where(c => c.Object.Name.Equals("usp_OrderList", StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.RelativePath, StringComparer.Ordinal).ToList();

            Assert.Equal(["admin/order/dashboard.asp", "admin/order/export.asp", "admin/order/summary.asp"], calls.Select(c => c.RelativePath));
            Assert.All(calls, c => Assert.Equal("dbo", c.Object.Schema));
            Assert.Equal("LoadOrderRows", calls.Single(c => c.RelativePath.EndsWith("summary.asp", StringComparison.Ordinal)).Procedure);
            Assert.Equal(3, calls.Single(c => c.RelativePath.EndsWith("export.asp", StringComparison.Ordinal)).Arguments);
            Assert.Null(calls.Single(c => c.RelativePath.EndsWith("dashboard.asp", StringComparison.Ordinal)).Procedure);
        }
        finally
        {
            File.Delete(cache);
        }
    }
}
