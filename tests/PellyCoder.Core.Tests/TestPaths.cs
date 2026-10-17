namespace PellyCoder.Core.Tests;

internal static class TestPaths
{
    /// <summary>Writes a Windows-style path (C:\a\b) so it is valid on the current OS.</summary>
    public static string P(string windowsPath) =>
        OperatingSystem.IsWindows()
            ? windowsPath
            : windowsPath[(windowsPath.IndexOf(':') + 1)..].Replace('\\', '/');
}
