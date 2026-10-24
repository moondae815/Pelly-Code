namespace PellyCoder.Core.Files;

/// <summary>The one path-key comparer shared by the spec transaction and the build graph.</summary>
public static class PathComparers
{
    /// <summary>Case-insensitive on Windows and macOS (their default file systems), ordinal elsewhere.</summary>
    public static StringComparer ForOs { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
