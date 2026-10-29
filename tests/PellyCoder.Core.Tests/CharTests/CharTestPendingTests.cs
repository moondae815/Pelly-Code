using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class CharTestPendingTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pending-ws"));
    private static readonly Workspace Ws = new(Root, Path.Combine(Root, ".pelly"));

    private static IReadOnlyList<PendingFile> Files() =>
    [
        new(Path.Combine(Root, "T", "CalcTests.cs"), "test", false, Path.Combine(Root, ".pelly", "backup", "CalcTests.cs.bak"), "ABC123", true),
        new(Path.Combine(Root, "T", "T.csproj"), "csproj", true, null, null, false),
    ];

    [Fact]
    public void WriteThenFind_RoundTrips()
    {
        var fs = new MockFileSystem();
        var pending = new CharTestPending(fs);

        var path = pending.Write(Ws, "run-1", Files());

        Assert.Equal(Path.Combine(Ws.PellyDir, "runs", "run-1", "chartest.pending"), path);
        Assert.True(fs.File.Exists(path));
        Assert.DoesNotContain(fs.AllFiles, f => f.EndsWith(".tmp", StringComparison.Ordinal));
        var found = Assert.Single(pending.FindAll(Ws));
        Assert.Equal(path, found.MarkerPath);
        Assert.Equal(Files(), found.Files);
    }

    [Fact]
    public void Delete_RemovesMarker()
    {
        var fs = new MockFileSystem();
        var pending = new CharTestPending(fs);
        var path = pending.Write(Ws, "run-1", Files());

        pending.Delete(path);

        Assert.False(fs.File.Exists(path));
        Assert.Empty(pending.FindAll(Ws));
    }

    [Fact]
    public void Find_Unreadable_StillReported()
    {
        var fs = new MockFileSystem();
        var marker = Path.Combine(Ws.PellyDir, "runs", "run-2", "chartest.pending");
        fs.AddFile(marker, new MockFileData("{ not json"));
        var pending = new CharTestPending(fs);

        var found = Assert.Single(pending.FindAll(Ws));

        Assert.Equal(marker, found.MarkerPath);
        Assert.Empty(found.Files);
        Assert.Contains(marker, CharTestPending.Describe([found]));
    }

    [Fact]
    public void Describe_ListsFilesBackupsAndMarker()
    {
        var fs = new MockFileSystem();
        var pending = new CharTestPending(fs);
        var path = pending.Write(Ws, "run-1", Files());

        var text = CharTestPending.Describe(pending.FindAll(Ws));

        Assert.Contains("이전 `pelly test` 실행이 끝나지 않았습니다", text);
        Assert.Contains("CalcTests.cs", text);
        Assert.Contains(".bak", text);
        Assert.Contains(path, text);
    }
}
