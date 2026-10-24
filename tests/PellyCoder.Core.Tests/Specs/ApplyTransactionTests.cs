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
        tx.Record(s0, s1);
        tx.Track(s1); // second track must not replace the first backup
        var s2 = await Write(s1, "three\n");
        tx.Record(s1, s2);

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
        tx.Record(a0, await Write(a0, "two\n"));
        tx.Record(b0, await Write(b0, "bee2\n"));
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
        tx.Record(a0, await Write(a0, "two\n"));

        var changes = tx.Changes();
        var c = Assert.Single(changes);
        Assert.Equal((p, "one\n", "two\n"), c);
    }

    [Fact]
    public async Task Edit_between_two_runs_on_one_file_is_not_rolled_back()
    {
        string p = Make("a.txt", "one\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-4");
        var s0 = _guard.Read(p);
        tx.Track(s0);
        var s1 = await Write(s0, "two\n");
        tx.Record(s0, s1);
        File.WriteAllText(p, "two-outside\n"); // saved outside pelly before the second run reads the file
        tx.Track(_guard.Read(p));
        var s1b = _guard.Read(p);
        var s2 = await Write(s1b, "three\n");
        tx.Record(s1b, s2);

        Assert.Equal([p], await tx.RollbackAsync(CancellationToken.None));
        Assert.Equal("three\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task Edit_between_pre_read_and_run_read_is_not_rolled_back()
    {
        string p = Make("a.txt", "one\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-5");
        tx.Track(_guard.Read(p));
        File.WriteAllText(p, "one-outside\n");
        var read = _guard.Read(p);
        var s1 = await Write(read, "two\n");
        tx.Record(read, s1);

        Assert.Equal([p], await tx.RollbackAsync(CancellationToken.None));
        Assert.Equal("two\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task Record_without_earlier_track_does_not_throw_and_reports_file_as_not_restored()
    {
        string p = Make("a.txt", "one\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-6");
        var s0 = _guard.Read(p);
        var s1 = await Write(s0, "two\n");
        tx.Record(s0, s1); // the pre-run read failed, and the disk no longer holds s0 to back up

        Assert.Equal([p], await tx.RollbackAsync(CancellationToken.None));
        Assert.Equal("two\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task Paths_differing_only_in_case_are_one_file_under_a_case_insensitive_comparer()
    {
        string p = Make("a.txt", "one\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-7", StringComparer.OrdinalIgnoreCase);
        var s0 = _guard.Read(p);
        tx.Track(s0);
        var s1 = await Write(s0, "two\n");
        tx.Record(s0, s1);
        string upper = Path.Combine(_root, "A.TXT");
        tx.Record(s1 with { Path = upper }, s1 with { Path = upper });

        Assert.Single(tx.Changes());
        Assert.Empty(await tx.RollbackAsync(CancellationToken.None));
        Assert.Equal("one\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task Rollback_continues_past_a_missing_backup_and_reports_it()
    {
        string p = Make("a.txt", "one\n");
        string q = Make("b.txt", "bee\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-8");
        var a0 = _guard.Read(p);
        var b0 = _guard.Read(q);
        tx.Track(a0);
        tx.Track(b0);
        tx.Record(a0, await Write(a0, "two\n"));
        tx.Record(b0, await Write(b0, "bee2\n"));
        File.Delete(Path.Combine(_ws.PellyDir, "backup", "apply-8", "a.txt"));

        var failed = await tx.RollbackAsync(CancellationToken.None);
        Assert.Equal([p], failed);
        Assert.Equal("two\n", File.ReadAllText(p));
        Assert.Equal("bee\n", File.ReadAllText(q));
    }

    [Fact]
    public void Path_comparer_ignores_case_on_windows_and_macos_only()
    {
        bool ignoresCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(ignoresCase, SpecTaskPaths.Comparer.Equals("db/a.sql", "DB/A.sql"));
    }

    [Fact]
    public void Full_path_of_a_task_normalizes_separators_under_the_root()
    {
        var path = new FileSystem().Path;
        string full = SpecTaskPaths.FullPath(path, _root, @"db\sub/a.sql");
        Assert.Equal(Path.Combine(_root, "db", "sub", "a.sql"), full);
    }
}
