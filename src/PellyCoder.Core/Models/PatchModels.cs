namespace PellyCoder.Core.Models;

public sealed record PatchBlock(string Search, string Replace);

public enum MatchStage { Exact = 1, TrailingWhitespace = 2, Indentation = 3 }

public static class PatchFailureKinds
{
    public const string Parse = "Parse";
    public const string NotFound = "NotFound";
    public const string Ambiguous = "Ambiguous";
    public const string OutsideRegion = "OutsideRegion";
    public const string Overlap = "Overlap";
    public const string Unencodable = "Unencodable";
}

public sealed record PatchFailure(string Kind, string Detail, string? NearestOriginal);

public sealed record PatchOutcome(bool Success, string? NewText, IReadOnlyList<MatchStage> Stages, PatchFailure? Failure);
