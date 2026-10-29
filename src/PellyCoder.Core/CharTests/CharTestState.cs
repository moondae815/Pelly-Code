using PellyCoder.Core.Files;

namespace PellyCoder.Core.CharTests;

/// <summary>
/// The <c>charTest</c> block of <c>summary.json</c> (design §4.6). <paramref name="RejectedBy"/> is the failure kind of the last
/// failed verdict (null when the last attempt passed every verdict); <paramref name="StableOnRerun"/> is null when no attempt reached the rerun.
/// </summary>
public sealed record CharTestSummary(int Requested, int Generated, int Kept, int PassedOnCurrent, bool? StableOnRerun, int KilledByMutation,
    string? RejectedBy, string Framework, bool NewFile);

/// <summary>
/// State of one <c>pelly test</c> run (set by <see cref="CharTestOrchestrator"/>): where the tests go, what was written, and what
/// the marker and the summary record. Files are tracked as snapshots: <c>*Original</c> as first read (null for a file pelly
/// creates), <c>*Current</c> as pelly last wrote it (null while the file is as it was).
/// </summary>
public sealed class CharTestState(int requested)
{
    public int Requested { get; } = requested;

    /// <summary>The single test project (full path).</summary>
    public string? TestProject { get; set; }

    public TestClassLocation? Location { get; set; }

    public FrameworkInfo? Framework { get; set; }

    /// <summary>Test method names (class-qualified, e.g. <c>CalcTests.Divide_Six_Three</c>) of the last generated output.</summary>
    public IReadOnlyList<string> NewTestNames { get; set; } = [];

    /// <summary>The test file as first read; null when the run creates it.</summary>
    public FileSnapshot? TestOriginal { get; set; }

    /// <summary>The test file as pelly last wrote it; null while it is as it was (or, for a new file, absent).</summary>
    public FileSnapshot? TestCurrent { get; set; }

    public string? TestBackupPath { get; set; }

    /// <summary>The legacy test csproj when a new test file needs a <c>Compile Include</c>; null otherwise.</summary>
    public FileSnapshot? CsprojOriginal { get; set; }

    public FileSnapshot? CsprojCurrent { get; set; }

    public string? CsprojBackupPath { get; set; }

    public string? TargetBackupPath { get; set; }

    /// <summary>The target as mutated and not yet restored; null when the target is the original.</summary>
    public FileSnapshot? TargetMutated { get; set; }

    /// <summary>True from just before the mutation is written until the target is verified restored.</summary>
    public bool TargetMutating { get; set; }

    /// <summary>Path of <c>chartest.pending</c>; null until the first write.</summary>
    public string? PendingPath { get; set; }

    /// <summary>The run decided to keep the test file (and csproj).</summary>
    public bool Kept { get; set; }

    public int Generated { get; set; }
    public int PassedOnCurrent { get; set; }
    public bool? StableOnRerun { get; set; }
    public int KilledByMutation { get; set; }
    public string? RejectedBy { get; set; }

    public bool NewFile => Location?.IsNew ?? false;

    public CharTestSummary Summary() => new(
        Requested, Generated, Kept ? NewTestNames.Count : 0, PassedOnCurrent, StableOnRerun, KilledByMutation, RejectedBy,
        Framework?.Framework.ToString() ?? "", NewFile);

    /// <summary>The marker entries for the files touched so far.</summary>
    public IReadOnlyList<PendingFile> PendingFiles(string targetPath, string targetOriginalSha)
    {
        var files = new List<PendingFile>();
        if (Location is { } location)
            files.Add(new PendingFile(location.FilePath, "test", location.IsNew, TestBackupPath, TestOriginal?.Sha256, false));
        if (CsprojOriginal is { } csproj)
            files.Add(new PendingFile(csproj.Path, "csproj", false, CsprojBackupPath, csproj.Sha256, false));
        if (TargetBackupPath is not null)
            files.Add(new PendingFile(targetPath, "target", false, TargetBackupPath, targetOriginalSha, TargetMutating));
        return files;
    }
}
