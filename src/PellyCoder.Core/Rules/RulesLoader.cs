using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Files;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Rules;

/// <summary>Reads the <c>## Pelly rules</c> section of the workspace root's AGENTS.md (subfolders are not read).</summary>
public static class RulesLoader
{
    public const string FileName = "AGENTS.md";
    public const int MaxBytes = 256 * 1024;

    public static RulesLoadResult Load(IFileSystem fs, Workspace ws)
    {
        var path = fs.Path.Combine(ws.Root, FileName);
        if (!fs.File.Exists(path)) return new RulesLoadResult(null, [], []);

        string text;
        try
        {
            if (fs.FileInfo.New(path).Length > MaxBytes)
                return Fail(path, $"{FileName} 크기가 상한 {MaxBytes / 1024} KiB를 넘습니다.");
            text = TextDecoding.Decode(fs.File.ReadAllBytes(path)).Replace("\r\n", "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Fail(path, $"{FileName}을(를) 읽을 수 없습니다: {ex.Message}");
        }

        var (rules, errors) = RulesDocument.Parse(text);
        return new RulesLoadResult(path, errors.Count > 0 ? [] : rules, errors);
    }

    private static RulesLoadResult Fail(string path, string message) => new(path, [], [new RulesError(0, message)]);
}
