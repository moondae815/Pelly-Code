using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Tests.Specs;

public sealed class SpecArchiverTests
{
    private static readonly DateOnly Day = new(2026, 10, 24);

    [Fact]
    public void Archive_moves_spec_into_dated_archive_name()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/ws/changes/x.md", "spec");
        var moved = SpecArchiver.Archive(fs, "/ws/changes/x.md", Day, "x");

        Assert.Equal(fs.Path.GetFullPath("/ws/changes/archive/2026-10-24-x.md"), moved);
        Assert.Equal("spec", fs.File.ReadAllText(moved));
        Assert.False(fs.File.Exists("/ws/changes/x.md"));
    }

    [Fact]
    public void Archive_appends_suffix_when_name_exists()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/ws/changes/archive/2026-10-24-x.md", "old");
        fs.AddFile("/ws/changes/x.md", "second");
        var second = SpecArchiver.Archive(fs, "/ws/changes/x.md", Day, "x");
        fs.AddFile("/ws/changes/x.md", "third");
        var third = SpecArchiver.Archive(fs, "/ws/changes/x.md", Day, "x");

        Assert.Equal(fs.Path.GetFullPath("/ws/changes/archive/2026-10-24-x-2.md"), second);
        Assert.Equal(fs.Path.GetFullPath("/ws/changes/archive/2026-10-24-x-3.md"), third);
        Assert.Equal("old", fs.File.ReadAllText("/ws/changes/archive/2026-10-24-x.md"));
        Assert.Equal("second", fs.File.ReadAllText(second));
        Assert.Equal("third", fs.File.ReadAllText(third));
    }
}
