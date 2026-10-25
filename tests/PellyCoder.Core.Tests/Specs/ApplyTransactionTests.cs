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
    public async Task Group_rollback_restores_group_start_not_apply_start()
    {
        string p = Make("a.txt", "one\n");
        string q = Make("b.txt", "bee\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-g1");
        var a0 = _guard.Read(p);
        tx.Track(a0);
        var a1 = await Write(a0, "two\n"); // T1, outside any group
        tx.Record(a0, a1);

        tx.BeginGroup("G1");
        tx.Track(a1);
        var a2 = await Write(a1, "three\n");
        tx.Record(a1, a2);
        var b0 = _guard.Read(q);
        tx.Track(b0);
        tx.Record(b0, await Write(b0, "bee2\n"));

        Assert.Empty(await tx.RollbackGroupAsync(CancellationToken.None));
        Assert.Equal("two\n", File.ReadAllText(p));
        Assert.Equal("bee\n", File.ReadAllText(q));
        Assert.True(File.Exists(Path.Combine(_ws.PellyDir, "backup", "apply-g1.G1", "a.txt")));
        Assert.True(File.Exists(Path.Combine(_ws.PellyDir, "backup", "apply-g1.G1", "b.txt")));
    }

    [Fact]
    public async Task Group_backups_do_not_collide_with_a_workspace_folder_named_like_the_group()
    {
        Directory.CreateDirectory(Path.Combine(_root, "G1"));
        string nested = Make(Path.Combine("G1", "x.cs"), "nested\n");
        string top = Make("x.cs", "top\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-g7");
        var n0 = _guard.Read(nested);
        tx.Track(n0); // apply backup lands at backup/apply-g7/G1/x.cs
        var n1 = await Write(n0, "nested1\n");
        tx.Record(n0, n1);

        tx.BeginGroup("G1");
        var t0 = _guard.Read(top);
        tx.Track(t0);
        tx.Record(t0, await Write(t0, "top2\n"));
        tx.Track(n1);
        tx.Record(n1, await Write(n1, "nested2\n"));

        Assert.Empty(await tx.RollbackGroupAsync(CancellationToken.None));
        Assert.Equal("top\n", File.ReadAllText(top));
        Assert.Equal("nested1\n", File.ReadAllText(nested));
    }

    [Fact]
    public async Task Group_rollback_skips_file_changed_outside()
    {
        string p = Make("a.txt", "one\n");
        string q = Make("b.txt", "bee\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-g2");
        tx.BeginGroup("G1");
        var a0 = _guard.Read(p);
        tx.Track(a0);
        var a1 = await Write(a0, "two\n");
        tx.Record(a0, a1);
        var b0 = _guard.Read(q);
        tx.Track(b0);
        tx.Record(b0, await Write(b0, "bee2\n"));
        File.WriteAllText(p, "two-outside\n"); // saved outside pelly before a re-run reads the file
        var read = _guard.Read(p);
        tx.Track(read);
        tx.Record(read, await Write(read, "three\n"));

        Assert.Equal([p], await tx.RollbackGroupAsync(CancellationToken.None));
        Assert.Equal("three\n", File.ReadAllText(p));
        Assert.Equal("bee\n", File.ReadAllText(q));
        Assert.Equal([p], await tx.RollbackAsync(CancellationToken.None)); // the full rollback skips it too
        Assert.Equal("three\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task Group_rollback_reports_a_file_edited_outside_after_the_last_record()
    {
        string p = Make("a.txt", "one\n");
        string q = Make("b.txt", "bee\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-g6");
        tx.BeginGroup("G1");
        var a0 = _guard.Read(p);
        tx.Track(a0);
        tx.Record(a0, await Write(a0, "two\n"));
        var b0 = _guard.Read(q);
        tx.Track(b0);
        tx.Record(b0, await Write(b0, "bee2\n"));
        File.WriteAllText(p, "two-outside\n"); // saved in an IDE while the group build ran: no task reads it again

        Assert.Equal([p], await tx.RollbackGroupAsync(CancellationToken.None));
        Assert.Equal("two-outside\n", File.ReadAllText(p));
        Assert.Equal("bee\n", File.ReadAllText(q));
    }

    [Fact]
    public async Task Full_rollback_after_group_rollback_returns_apply_originals()
    {
        string p = Make("a.txt", "one\n");
        string q = Make("b.txt", "bee\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-g3");
        var a0 = _guard.Read(p);
        tx.Track(a0);
        var a1 = await Write(a0, "two\n");
        tx.Record(a0, a1);

        tx.BeginGroup("G1");
        tx.Track(a1);
        tx.Record(a1, await Write(a1, "three\n"));
        var b0 = _guard.Read(q);
        tx.Track(b0);
        tx.Record(b0, await Write(b0, "bee2\n"));
        Assert.Empty(await tx.RollbackGroupAsync(CancellationToken.None));

        Assert.Equal([(p, "one\n", "two\n")], tx.Changes());
        Assert.Empty(await tx.RollbackAsync(CancellationToken.None));
        Assert.Equal("one\n", File.ReadAllText(p));
        Assert.Equal("bee\n", File.ReadAllText(q));
    }

    [Fact]
    public async Task Group_changes_compare_against_group_start()
    {
        string p = Make("a.txt", "one\n");
        string q = Make("b.txt", "bee\n");
        string r = Make("c.txt", "sea\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-g4");
        var a0 = _guard.Read(p);
        tx.Track(a0);
        var a1 = await Write(a0, "two\n");
        tx.Record(a0, a1);
        var c0 = _guard.Read(r);
        tx.Track(c0);
        tx.Record(c0, await Write(c0, "sea2\n")); // changed before the group, untouched inside it

        tx.BeginGroup("G1");
        Assert.Empty(tx.GroupChanges());
        tx.Track(a1);
        tx.Record(a1, await Write(a1, "three\n"));
        var b0 = _guard.Read(q);
        tx.Track(b0);
        tx.Record(b0, b0); // read but left unchanged

        Assert.Equal([(p, "two\n", "three\n")], tx.GroupChanges());
        tx.EndGroup();
        Assert.Empty(tx.GroupChanges());
    }

    [Fact]
    public async Task Group_rollback_reports_a_file_whose_group_backup_is_missing()
    {
        string p = Make("a.txt", "one\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-g5");
        var a0 = _guard.Read(p);
        tx.Track(a0);
        var a1 = await Write(a0, "two\n");
        tx.Record(a0, a1);
        tx.BeginGroup("G1");
        tx.Track(a1);
        tx.Record(a1, await Write(a1, "three\n"));
        File.Delete(Path.Combine(_ws.PellyDir, "backup", "apply-g5.G1", "a.txt"));

        Assert.Equal([p], await tx.RollbackGroupAsync(CancellationToken.None));
        Assert.Equal("three\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task Group_rollback_closes_the_group()
    {
        string p = Make("a.txt", "one\n");
        string q = Make("b.txt", "bee\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-g8");
        tx.BeginGroup("G1");
        var a0 = _guard.Read(p);
        tx.Track(a0);
        tx.Record(a0, await Write(a0, "two\n"));

        Assert.Empty(await tx.RollbackGroupAsync(CancellationToken.None));

        // Closed: a later file gets no group backup, and nothing is left for a second group rollback or the group diff.
        var b0 = _guard.Read(q);
        tx.Track(b0);
        tx.Record(b0, await Write(b0, "bee2\n"));
        Assert.False(File.Exists(Path.Combine(_ws.PellyDir, "backup", "apply-g8.G1", "b.txt")));
        Assert.Empty(tx.GroupChanges());
        Assert.Empty(await tx.RollbackGroupAsync(CancellationToken.None));
        Assert.Equal("bee2\n", File.ReadAllText(q));
        tx.BeginGroup("G2"); // a new group opens normally
    }

    [Fact]
    public async Task Failed_group_backup_in_track_leaves_the_file_untracked_by_the_group()
    {
        string p = Make("a.txt", "one\n");
        var tx = new ApplyTransaction(_guard, _ws, "apply-g9");
        tx.BeginGroup("G1");
        // A file where the group backup folder should be: the group backup throws, the apply backup does not.
        Directory.CreateDirectory(Path.Combine(_ws.PellyDir, "backup"));
        string blocker = Path.Combine(_ws.PellyDir, "backup", "apply-g9.G1");
        File.WriteAllText(blocker, "");
        var a0 = _guard.Read(p);

        Assert.ThrowsAny<IOException>(() => tx.Track(a0));
        Assert.Empty(tx.GroupChanges());
        Assert.Empty(tx.Changes());

        // Tracking again once the backup can be taken records the group start as it was, so the group rollback restores it.
        File.Delete(blocker);
        tx.Track(a0);
        tx.Record(a0, await Write(a0, "two\n"));
        Assert.Equal([(p, "one\n", "two\n")], tx.GroupChanges());
        Assert.Empty(await tx.RollbackGroupAsync(CancellationToken.None));
        Assert.Equal("one\n", File.ReadAllText(p));
    }

    [Fact]
    public void Group_id_must_be_one_unused_path_segment()
    {
        var tx = new ApplyTransaction(_guard, _ws, "apply-g6");
        Assert.Throws<ArgumentException>(() => tx.BeginGroup("../G1"));
        Assert.Throws<ArgumentException>(() => tx.BeginGroup(@"a\b"));
        Assert.Throws<ArgumentException>(() => tx.BeginGroup(".."));
        Assert.Throws<ArgumentException>(() => tx.BeginGroup(""));
        tx.BeginGroup("G1");
        tx.EndGroup();
        Assert.Throws<InvalidOperationException>(() => tx.BeginGroup("G1"));
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
