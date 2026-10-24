using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Files;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

public sealed class ApplyTransactionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pelly-tx-").FullName;
    private readonly FileGuard _guard = new(new FileSystem(), TimeProvider.System);
    private readonly Workspace _ws;

    public ApplyTransactionTests() => _ws = new Workspace(_root, Path.Combine(_root, ".pelly"));

    public void Dispose() => Directory.Delete(_root, true);

    private string Make(string name, string text)
    {
        string p = Path.Combine(_root, name);
        File.WriteAllText(p, text, new UTF8Encoding(false));
        return p;
    }

    private async Task<FileSnapshot> Write(FileSnapshot current, string text)
    {
        var r = await _guard.WriteAsync(current, text, CancellationToken.None);
        Assert.Equal(FileWriteStatus.Written, r.Status);
        return r.Snapshot!;
    }

    [Fact]
    public async Task Two_updates_to_one_file_rollback_to_first_original()
    {
        string p = Make("a.txt", "one\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-1");
        var s0 = _guard.Read(p);
        tx.Track(s0);
        var s1 = await Write(s0, "two\n");
        tx.Update(s1);
        tx.Track(s1); // second track must not replace the first backup
        var s2 = await Write(s1, "three\n");
        tx.Update(s2);

        Assert.Empty(await tx.RollbackAsync(CancellationToken.None));
        Assert.Equal("one\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task Rollback_skips_externally_changed_file_and_reports_it()
    {
        string p = Make("a.txt", "one\n");
        string q = Make("b.txt", "bee\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-2");
        var a0 = _guard.Read(p);
        var b0 = _guard.Read(q);
        tx.Track(a0);
        tx.Track(b0);
        tx.Update(await Write(a0, "two\n"));
        tx.Update(await Write(b0, "bee2\n"));
        File.WriteAllText(p, "outside\n");

        var failed = await tx.RollbackAsync(CancellationToken.None);
        Assert.Equal([p], failed);
        Assert.Equal("outside\n", File.ReadAllText(p));
        Assert.Equal("bee\n", File.ReadAllText(q));
    }

    [Fact]
    public async Task Changes_lists_only_modified_files()
    {
        string p = Make("a.txt", "one\n");
        string q = Make("b.txt", "bee\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-3");
        var a0 = _guard.Read(p);
        tx.Track(a0);
        tx.Track(_guard.Read(q));
        tx.Update(await Write(a0, "two\n"));

        var changes = tx.Changes();
        var c = Assert.Single(changes);
        Assert.Equal((p, "one\n", "two\n"), c);
    }
}
