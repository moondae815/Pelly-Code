using System.IO.Abstractions;
using System.Xml;
using System.Xml.Linq;
using PellyCoder.Core.Builds;

namespace PellyCoder.Core.Doctor;

/// <summary>The <c>타겟팅 팩</c> row of <c>pelly doctor --project</c>: is the reference-assembly pack of a legacy project installed?</summary>
public static class TargetingPackCheck
{
    public const string Name = "타겟팅 팩";

    /// <param name="programFilesX86">Value of <c>%ProgramFiles(x86)%</c>, read by the caller (Core never reads the environment).</param>
    /// <remarks>Windows 미확인: the reference-assembly folder lookup is only verified on windows-latest CI.</remarks>
    public static DoctorCheck Run(IFileSystem fs, Func<bool> isWindows, string? programFilesX86, string projectPath)
    {
        try
        {
            if (ProjectStyleDetector.Detect(fs, projectPath) == ProjectStyle.Sdk)
                return new(Name, DoctorStatus.Ok, "SDK 스타일 — NuGet 참조 어셈블리 사용", null);

            var version = ReadFrameworkVersion(fs, projectPath);
            if (version is null)
                return new(Name, DoctorStatus.Warn, "TargetFrameworkVersion을 찾을 수 없습니다", "csproj의 TargetFrameworkVersion을 확인하세요");
            if (!isWindows())
                return new(Name, DoctorStatus.Warn, "Windows에서만 확인합니다", null);
            if (string.IsNullOrWhiteSpace(programFilesX86))
                return new(Name, DoctorStatus.Warn, "ProgramFiles(x86) 경로를 알 수 없습니다", null);

            var folder = fs.Path.Combine(programFilesX86, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", version);
            if (fs.Directory.Exists(folder))
                return new(Name, DoctorStatus.Ok, $"{version} ({folder})", null);
            var digits = version.TrimStart('v', 'V').Replace(".", "");
            return new(Name, DoctorStatus.Fail, $"{version} 참조 어셈블리 폴더가 없습니다: {folder}",
                $"Developer Pack을 설치하거나 Microsoft.NETFramework.ReferenceAssemblies.net{digits} 패키지를 쓰세요");
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return new(Name, DoctorStatus.Fail, $"프로젝트 파일을 읽을 수 없습니다: {ex.Message}", "--project 경로와 csproj 내용을 확인하세요");
        }
    }

    private static string? ReadFrameworkVersion(IFileSystem fs, string projectPath)
    {
        var root = XDocument.Parse(fs.File.ReadAllText(projectPath)).Root;
        var text = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFrameworkVersion" && !e.HasElements)?.Value.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
