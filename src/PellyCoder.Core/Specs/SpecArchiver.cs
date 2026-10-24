using System.Globalization;
using System.IO.Abstractions;

namespace PellyCoder.Core.Specs;

/// <summary>Moves a finished spec to <c>changes/archive/&lt;yyyy-MM-dd&gt;-&lt;id&gt;.md</c>, adding <c>-2</c>, <c>-3</c>… when taken.</summary>
public static class SpecArchiver
{
    /// <summary>Returns the full path the spec was moved to.</summary>
    public static string Archive(IFileSystem fs, string specPath, DateOnly date, string id)
    {
        var full = fs.Path.GetFullPath(specPath);
        var dir = fs.Path.Combine(fs.Path.GetDirectoryName(full)!, "archive");
        fs.Directory.CreateDirectory(dir);
        var stem = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + id;
        var dest = fs.Path.Combine(dir, stem + ".md");
        for (int n = 2; fs.File.Exists(dest); n++)
            dest = fs.Path.Combine(dir, $"{stem}-{n}.md");
        fs.File.Move(full, dest);
        return dest;
    }
}
