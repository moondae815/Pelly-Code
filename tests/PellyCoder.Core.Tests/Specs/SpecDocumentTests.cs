using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Files;
using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Tests.Specs;

public sealed class SpecDocumentTests
{
    private const string Path = "/ws/changes/x.md";

    private const string Text =
        "---\nid: x\nstatus: draft   # keep\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" +
        "- [ ] T1 cs A.cs#M 요청 (AC1)\n- [ ] T2 sql db/a.sql 둘째\n";

    private static (MockFileSystem Fs, SpecDocument Doc) Open(byte[] bytes)
    {
        var fs = new MockFileSystem();
        fs.AddFile(Path, new MockFileData(bytes));
        return (fs, SpecDocument.Load(new FileGuard(fs, TimeProvider.System), Path));
    }

    private static (MockFileSystem Fs, SpecDocument Doc) Open(string text = Text) =>
        Open(Encoding.UTF8.GetBytes(text));

    private static string Disk(MockFileSystem fs) => Encoding.UTF8.GetString(fs.File.ReadAllBytes(Path));

    [Fact]
    public async Task Mark_done_sets_x_and_run_comment()
    {
        var (fs, doc) = Open();
        var r = await doc.MarkDoneAsync("T1", "R1", CancellationToken.None);
        Assert.Equal(FileWriteStatus.Written, r.Status);
        Assert.Contains("- [x] T1 cs A.cs#M 요청 (AC1) <!-- run: R1 -->\n", Disk(fs));
        Assert.True(doc.Spec.Tasks[0].Done);
        Assert.Equal("R1", doc.Spec.Tasks[0].RunId);
    }

    [Fact]
    public async Task Mark_failed_then_done_replaces_comment()
    {
        var (fs, doc) = Open();
        await doc.MarkFailedAsync("T1", "R1", "VerifyFailed", CancellationToken.None);
        Assert.Contains("- [ ] T1 cs A.cs#M 요청 (AC1) <!-- failed: R1 VerifyFailed -->\n", Disk(fs));
        Assert.Equal("VerifyFailed", doc.Spec.Tasks[0].Failure);
        await doc.MarkDoneAsync("T1", "R2", CancellationToken.None);
        string disk = Disk(fs);
        Assert.Contains("- [x] T1 cs A.cs#M 요청 (AC1) <!-- run: R2 -->\n", disk);
        Assert.DoesNotContain("failed", disk);
    }

    [Fact]
    public async Task Clear_marks_restores_unchecked_without_comments()
    {
        var (fs, doc) = Open();
        await doc.MarkDoneAsync("T1", "R1", CancellationToken.None);
        await doc.MarkFailedAsync("T2", "R1", "X", CancellationToken.None);
        await doc.ClearMarksAsync(["T1", "T2"], CancellationToken.None);
        Assert.Equal(Text, Disk(fs));
    }

    [Fact]
    public async Task Set_status_rewrites_front_matter_only()
    {
        var (fs, doc) = Open();
        var r = await doc.SetStatusAsync(SpecStatus.InProgress, CancellationToken.None);
        Assert.Equal(FileWriteStatus.Written, r.Status);
        Assert.Equal(Text.Replace("status: draft   # keep", "status: in-progress   # keep"), Disk(fs));
        Assert.Equal(SpecStatus.InProgress, doc.Spec.Status);
    }

    [Fact]
    public async Task Mark_refuses_when_spec_changed_on_disk()
    {
        var (fs, doc) = Open();
        fs.File.WriteAllText(Path, Text + "\n외부 편집\n");
        string before = Disk(fs);
        var r = await doc.MarkDoneAsync("T1", "R1", CancellationToken.None);
        Assert.Equal(FileWriteStatus.ExternallyChanged, r.Status);
        Assert.Equal(before, Disk(fs));
    }

    [Fact]
    public async Task Mark_preserves_crlf_bom_and_cp949()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp949 = Encoding.GetEncoding(949);
        var utf8Bom = new UTF8Encoding(true);
        var cases = new (byte[] Bytes, Encoding Enc, bool Bom, string NewLine)[]
        {
            (utf8Bom.GetPreamble().Concat(Encoding.UTF8.GetBytes(Text.Replace("\n", "\r\n"))).ToArray(), Encoding.UTF8, true, "\r\n"),
            (cp949.GetBytes(Text), cp949, false, "\n"),
            (Encoding.UTF8.GetBytes(Text), Encoding.UTF8, false, "\n"),
        };
        foreach (var (bytes, enc, bom, nl) in cases)
        {
            var (fs, doc) = Open(bytes);
            var r = await doc.MarkDoneAsync("T1", "R1", CancellationToken.None);
            Assert.Equal(FileWriteStatus.Written, r.Status);
            var after = fs.File.ReadAllBytes(Path);
            var snap = TextFileCodec.Decode(Path, after);
            Assert.Equal(bom, snap.HasBom);
            Assert.Equal(nl, snap.NewLine);
            Assert.Equal(enc.CodePage, snap.Encoding.CodePage);
            Assert.Contains("- [x] T1 cs A.cs#M 요청 (AC1) <!-- run: R1 -->", snap.Text);
        }
    }
}
