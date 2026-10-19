namespace PellyCoder.Eval;

/// <summary>The <c>--out</c> checks <c>run</c> and <c>export</c> share before they write anything.</summary>
public static class OutputFolder
{
    /// <summary>
    /// Null when <paramref name="path"/> is missing or an empty folder. Otherwise why it cannot be used: it is a file, it cannot be
    /// read, or it is not empty (<paramref name="nonEmptyMessage"/>).
    /// </summary>
    public static string? Problem(string path, string nonEmptyMessage)
    {
        try
        {
            if (File.Exists(path)) return $"--out이 폴더가 아니라 파일입니다: {path}";
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() ? nonEmptyMessage : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"--out 폴더를 읽을 수 없습니다: {path}: {ex.Message}";
        }
    }
}
