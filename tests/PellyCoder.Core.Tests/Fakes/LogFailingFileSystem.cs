using System.IO.Abstractions;

namespace PellyCoder.Core.Tests.Fakes;

/// <summary>Real file system whose writes under <c>.pelly/runs</c> throw a non-I/O exception (a logging bug).</summary>
public sealed class LogFailingFileSystem : FileSystem, IFileSystem
{
    private readonly IFile _file;

    public LogFailingFileSystem() => _file = new LogFailingFile(this);

    IFile IFileSystem.File => _file;

    private sealed class LogFailingFile(IFileSystem fs) : FileWrapper(fs)
    {
        public override void WriteAllText(string path, string? contents)
        {
            ThrowIfRunLog(path);
            base.WriteAllText(path, contents);
        }

        public override void AppendAllText(string path, string? contents)
        {
            ThrowIfRunLog(path);
            base.AppendAllText(path, contents);
        }

        private static void ThrowIfRunLog(string path)
        {
            var sep = System.IO.Path.DirectorySeparatorChar;
            if (path.Contains($"{sep}.pelly{sep}runs{sep}", StringComparison.Ordinal))
                throw new InvalidOperationException("scripted log failure");
        }
    }
}
