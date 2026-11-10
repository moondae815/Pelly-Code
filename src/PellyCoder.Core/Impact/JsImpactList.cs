using System.IO.Abstractions;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PellyCoder.Core.Impact;

/// <summary>The full list of a <c>spec check --impact</c> JS search: <c>.pelly/impact/&lt;spec id&gt;-js.json</c> (W3 design §4.4).</summary>
public static class JsImpactList
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(IFileSystem fs, string path, string? specId, IReadOnlyList<JsImpactSeed> seeds, IReadOnlyList<JsImpactHit> hits)
    {
        ArgumentNullException.ThrowIfNull(fs);
        var dir = fs.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) fs.Directory.CreateDirectory(dir);
        fs.File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            specId,
            seeds = seeds.Select(s => new { task = s.TaskId, path = s.RelativePath, function = s.Function }),
            calls = hits.Select(h => new
            {
                task = h.Seed.TaskId,
                function = h.Seed.Function,
                path = h.RelativePath.Replace('\\', '/'),
                line = h.Call.Line,
                arguments = h.Call.Arguments,
                reference = h.Call.Reference,
                source = h.Call.Source switch { Web.JsCallSource.PageScript => "script", Web.JsCallSource.Attribute => h.Call.AttributeName ?? "attribute", _ => "js" },
                coveredBy = h.CoveredBy,
                pageDoesNotLoadSeedFile = h.PageDoesNotLoadSeedFile,
            }),
        }, Json));
    }
}
