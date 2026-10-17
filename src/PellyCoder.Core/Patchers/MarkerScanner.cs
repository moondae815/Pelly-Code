namespace PellyCoder.Core.Patchers;

public static class MarkerScanner
{
    private static readonly string[] Markers = ["<<<<<<<", "=======", ">>>>>>>"];

    /// <summary>True when any line of the LF-normalized text starts with a merge-conflict marker.</summary>
    public static bool HasConflictMarkers(string textLf)
    {
        foreach (var line in textLf.Split('\n'))
        {
            foreach (var marker in Markers)
            {
                if (line.StartsWith(marker, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }
}
