using System.IO.Abstractions;

namespace PellyCoder.Core.Files;

/// <summary>The bounded source reader the workspace scans share (spec checks, test method index).</summary>
public static class BoundedSources
{
    public const int MaxDirectories = 2000;
    public const int MaxFiles = 5000;
    public const int MaxFileBytes = 512 * 1024;

    /// <summary>
    /// Lazily yields path and text of the files matching <paramref name="pattern"/> under <paramref name="dir"/>: at most
    /// <see cref="MaxFiles"/> files from a <see cref="BoundedWalker"/> walk of <see cref="MaxDirectories"/> directories;
    /// a file over <see cref="MaxFileBytes"/> or one that cannot be read is skipped.
    /// </summary>
    public static IEnumerable<(string Path, string Text)> Read(IFileSystem fs, string dir, string pattern)
    {
        foreach (var path in BoundedWalker.Files(fs, dir, pattern, MaxDirectories).Take(MaxFiles))
        {
            string text;
            try
            {
                if (fs.FileInfo.New(path).Length > MaxFileBytes) continue;
                text = TextFileCodec.Read(fs, path).Text;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { continue; }
            yield return (path, text);
        }
    }
}
