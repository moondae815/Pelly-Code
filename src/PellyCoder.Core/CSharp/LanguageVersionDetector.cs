using System.IO.Abstractions;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;

namespace PellyCoder.Core.CSharp;

/// <summary>The C# language version a project compiles with, plus the hint put in the prompt.</summary>
public sealed record LanguageTarget(LanguageVersion Version, string Hint);

/// <summary>Detects the C# language version from a csproj (design 8.5).</summary>
public static partial class LanguageVersionDetector
{
    private const string NoProjectHint = "C# (latest)";

    public static LanguageTarget Detect(IFileSystem fs, string? csprojPath)
    {
        if (csprojPath is null)
            return new LanguageTarget(LanguageVersion.Default, NoProjectHint);

        var root = XDocument.Parse(fs.File.ReadAllText(csprojPath)).Root;
        if (root is null)
            return new LanguageTarget(LanguageVersion.Default, NoProjectHint);

        var isLegacy = root.Attribute("Sdk") is null && root.Elements().All(e => e.Name.LocalName != "Sdk");
        var props = LoadNearestProps(fs, csprojPath);
        string? Effective(string name) => Property(root, name) ?? PropsProperty(props, name);

        var explicitVersion = ParseLangVersion(Effective("LangVersion"));

        var frameworks = isLegacy
            ? LegacyFramework(Effective("TargetFrameworkVersion"))
            : (Effective("TargetFrameworks") ?? Effective("TargetFramework"))
                ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
              ?? [];

        // Lowest default version wins; ties keep the first listed TFM.
        var chosen = frameworks
            .Select(tfm => (Tfm: tfm, Default: TfmDefault(tfm)))
            .Where(x => x.Default is not null)
            .OrderBy(x => x.Default)
            .Select(x => ((string Tfm, LanguageVersion? Default)?)x)
            .FirstOrDefault();

        var hintFramework = chosen?.Tfm ?? frameworks.FirstOrDefault();
        var version = explicitVersion ?? chosen?.Default ?? (isLegacy ? LanguageVersion.CSharp7_3 : LanguageVersion.Default);

        var versionText = version == LanguageVersion.Default ? "(latest)" : VersionText(version);
        var hint = hintFramework is null
            ? $"C# {versionText}"
            : $"C# {versionText}, {FrameworkText(hintFramework)}";
        return new LanguageTarget(version, hint);
    }

    /// <summary>First Directory.Build.props from the csproj folder upwards (MSBuild: nearest wins, no chaining).</summary>
    private static (string Path, XElement Root)? LoadNearestProps(IFileSystem fs, string csprojPath)
    {
        var dir = fs.Path.GetDirectoryName(fs.Path.GetFullPath(csprojPath));
        while (dir is not null)
        {
            var candidate = fs.Path.Combine(dir, "Directory.Build.props");
            if (fs.File.Exists(candidate))
            {
                try
                {
                    var root = XDocument.Parse(fs.File.ReadAllText(candidate)).Root;
                    return root is null ? null : (candidate, root);
                }
                catch (System.Xml.XmlException ex)
                {
                    throw new System.Xml.XmlException($"{candidate}: {ex.Message}", ex);
                }
            }

            dir = fs.Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>Unconditioned property directly inside an unconditioned PropertyGroup; last one wins.</summary>
    private static string? PropsProperty((string Path, XElement Root)? props, string name) =>
        props?.Root.Elements().Where(g => g.Name.LocalName == "PropertyGroup" && g.Attribute("Condition") is null)
            .SelectMany(g => g.Elements())
            .LastOrDefault(e => e.Name.LocalName == name && e.Attribute("Condition") is null && !e.HasElements)
            ?.Value.Trim();

    private static string? Property(XElement root, string name) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == name && !e.HasElements)?.Value.Trim();

    private static List<string> LegacyFramework(string? frameworkVersion)
    {
        if (string.IsNullOrWhiteSpace(frameworkVersion))
            return [];
        // v4.5 -> net45, v4.6.2 -> net462
        return [$"net{frameworkVersion.TrimStart('v', 'V').Replace(".", "")}"];
    }

    private static LanguageVersion? ParseLangVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !char.IsDigit(value[0]))
            return null; // latest / preview / latestMajor / default -> TFM default

        return LanguageVersionFacts.TryParse(value, out var parsed) ? parsed : null;
    }

    private static LanguageVersion? TfmDefault(string tfm)
    {
        var name = tfm.Trim().ToLowerInvariant();
        var dash = name.IndexOf('-');
        if (dash >= 0)
            name = name[..dash];

        if (NetFrameworkTfm().IsMatch(name))
            return LanguageVersion.CSharp7_3;

        var modern = ModernTfm().Match(name);
        if (modern.Success)
        {
            var kind = modern.Groups["kind"].Value;
            var major = int.Parse(modern.Groups["major"].Value);
            var minor = int.Parse(modern.Groups["minor"].Value);
            switch (kind)
            {
                case "net" when major >= 5:
                    return major switch
                    {
                        5 => LanguageVersion.CSharp9,
                        6 => LanguageVersion.CSharp10,
                        7 => LanguageVersion.CSharp11,
                        8 => LanguageVersion.CSharp12,
                        9 => LanguageVersion.CSharp13,
                        10 => LanguageVersion.CSharp14,
                        _ => null,
                    };
                case "netcoreapp":
                    return major >= 3 ? LanguageVersion.CSharp8 : LanguageVersion.CSharp7_3;
                case "netstandard":
                    return major == 2 && minor >= 1 ? LanguageVersion.CSharp8 : LanguageVersion.CSharp7_3;
            }
        }

        return null;
    }

    private static string FrameworkText(string tfm)
    {
        var name = tfm.Trim().ToLowerInvariant();
        var match = NetFrameworkTfm().Match(name);
        return match.Success
            ? $".NET Framework {string.Join('.', match.Groups["digits"].Value.ToCharArray())}"
            : tfm.Trim();
    }

    private static string VersionText(LanguageVersion version)
    {
        var number = (int)version;
        if (number < 700)
            return version.ToString();
        return number % 100 == 0 ? (number / 100).ToString() : $"{number / 100}.{number % 100}";
    }

    [GeneratedRegex(@"^net(?<digits>\d{2,3})$")]
    private static partial Regex NetFrameworkTfm();

    [GeneratedRegex(@"^(?<kind>netcoreapp|netstandard|net)(?<major>\d+)\.(?<minor>\d+)$")]
    private static partial Regex ModernTfm();
}
