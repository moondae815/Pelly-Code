using System.IO.Abstractions;

namespace PellyCoder.Core.CSharp;

/// <summary>Finds the project file that owns a source file.</summary>
public static class ProjectFileLocator
{
    /// <summary>Walks up from the file's folder and returns the first <c>*.csproj</c>, or null when none exists.</summary>
    public static string? FindNearestCsproj(IFileSystem fs, string filePath)
    {
        var dir = fs.Path.GetDirectoryName(fs.Path.GetFullPath(filePath));
        while (!string.IsNullOrEmpty(dir))
        {
            if (fs.Directory.Exists(dir))
            {
                var project = fs.Directory.GetFiles(dir, "*.csproj").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
                if (project is not null)
                    return project;
            }

            dir = fs.Path.GetDirectoryName(dir);
        }

        return null;
    }
}
