using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.Testing;

public class TestDeclarationHashTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private const string Source = """
        namespace Shop.Tests;
        class OrderTests
        {
            private int _seed = 1;

            [Fact]
            public void Cancel_Throws() { Assert.Equal(1, _seed); }

            [Fact]
            public void Other() { }
        }
        """;

    private static MockFileSystem Fs(string text)
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("Shop.Tests/OrderTests.cs"), new MockFileData(text));
        return fs;
    }

    [Fact]
    public void Of_SameDeclaration_SameHash()
    {
        var hash = TestDeclarationHash.Of(Fs(Source), Root, "Cancel_Throws");

        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, TestDeclarationHash.Of(Fs(Source), Root, "Cancel_Throws"));
        Assert.Equal(hash, TestDeclarationHash.Of(Fs(Source), Root, "OrderTests.Cancel_Throws"));
    }

    [Fact]
    public void Of_BodyChanged_DifferentHash()
    {
        var before = TestDeclarationHash.Of(Fs(Source), Root, "Cancel_Throws");

        var after = TestDeclarationHash.Of(Fs(Source.Replace("Assert.Equal(1, _seed)", "Assert.Equal(2, _seed)")), Root, "Cancel_Throws");

        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Of_OtherMemberInFileChanged_SameHash()
    {
        var before = TestDeclarationHash.Of(Fs(Source), Root, "Cancel_Throws");
        var changed = Source.Replace("_seed = 1", "_seed = 2").Replace("public void Other() { }", "public void Other() { var x = 1; }");

        Assert.Equal(before, TestDeclarationHash.Of(Fs(changed), Root, "Cancel_Throws"));
    }

    [Fact]
    public void Of_NotFound_Null()
    {
        Assert.Null(TestDeclarationHash.Of(Fs(Source), Root, "Missing_Test"));
        Assert.Null(TestDeclarationHash.Of(Fs(Source.Replace("[Fact]\n    public void Cancel_Throws", "public void Cancel_Throws")), Root, "Cancel_Throws"));
    }

    [Fact]
    public void Of_MatchesTargetFileHashOfTestFirstState()
    {
        var fs = Fs(Source);
        var file = P("Shop.Tests/OrderTests.cs");

        Assert.Equal(PellyCoder.Core.Fsm.TestFirstState.HashOf(fs, file, file, ["Shop.Tests.OrderTests.Cancel_Throws"]),
            TestDeclarationHash.Of(fs, Root, "Cancel_Throws"));
    }
    [Fact]
    public void TryOf_SameAsOf()
    {
        Assert.Equal(TestDeclarationHash.Of(Fs(Source), Root, "Cancel_Throws"), TestDeclarationHash.TryOf(Fs(Source), Root, "Cancel_Throws"));
        Assert.Null(TestDeclarationHash.TryOf(Fs(Source), Root, "Missing_Test"));
        Assert.Equal(P("Shop.Tests/OrderTests.cs"), TestDeclarationHash.TryDeclaringFile(Fs(Source), Root, "Cancel_Throws"));
        Assert.Null(TestDeclarationHash.TryDeclaringFile(Fs(Source), Root, "Missing_Test"));
    }

    [Fact]
    public void TryOf_FindThrows_Null()
    {
        // The declaring project lookup lists the folder for *.csproj; an IOException there must not escape.
        var fs = new FailingProjectListing(Fs(Source), new IOException("busy"));

        Assert.ThrowsAny<IOException>(() => TestDeclarationHash.Of(fs, Root, "Cancel_Throws"));
        Assert.Null(TestDeclarationHash.TryOf(fs, Root, "Cancel_Throws"));
        Assert.Null(TestDeclarationHash.TryDeclaringFile(fs, Root, "Cancel_Throws"));
        Assert.Null(TestDeclarationHash.TryOf(new FailingProjectListing(Fs(Source), new UnauthorizedAccessException("denied")), Root, "Cancel_Throws"));
    }

    [Fact]
    public void TryOf_FileUnreadable_Null()
    {
        var fs = new FailingRead(Fs(Source));

        Assert.Null(TestDeclarationHash.TryOf(fs, Root, "Cancel_Throws"));
    }

    /// <summary>A file system whose <c>*.csproj</c> listing throws (the walk itself lists <c>*.cs</c> and still works).</summary>
    internal sealed class FailingProjectListing(MockFileSystem inner, Exception error) : Wrapped(inner)
    {
        private readonly IDirectory _directory = new ThrowingDirectory(inner, error);
        public override IDirectory Directory => _directory;

        private sealed class ThrowingDirectory(MockFileSystem inner, Exception error) : MockDirectory(inner, inner.File as FileBase, Root)
        {
            public override string[] GetFiles(string path, string searchPattern) =>
                searchPattern == "*.csproj" ? throw error : base.GetFiles(path, searchPattern);
        }
    }

    /// <summary>A file system whose <c>File.ReadAllText</c> throws (the walk reads through streams and still works).</summary>
    internal sealed class FailingRead(MockFileSystem inner) : Wrapped(inner)
    {
        private readonly IFile _file = new ThrowingFile(inner);
        public override IFile File => _file;

        private sealed class ThrowingFile(MockFileSystem inner) : MockFile(inner)
        {
            public override string ReadAllText(string path) => throw new IOException("locked");
        }
    }

    internal abstract class Wrapped(MockFileSystem inner) : IFileSystem
    {
        public virtual IFile File => inner.File;
        public virtual IDirectory Directory => inner.Directory;
        public IFileInfoFactory FileInfo => inner.FileInfo;
        public IFileStreamFactory FileStream => inner.FileStream;
        public IPath Path => inner.Path;
        public IDirectoryInfoFactory DirectoryInfo => inner.DirectoryInfo;
        public IDriveInfoFactory DriveInfo => inner.DriveInfo;
        public IFileSystemWatcherFactory FileSystemWatcher => inner.FileSystemWatcher;
        public IFileVersionInfoFactory FileVersionInfo => inner.FileVersionInfo;
        public IRandomAccess RandomAccess => inner.RandomAccess;
    }
}
