using System.IO.Abstractions;
using PellyCoder.Core.Doctor;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Doctor;

public sealed class TargetingPackWindowsTests
{
    [WindowsFact]
    public void TargetingPack_LegacyV48_Ok()
    {
        var dir = Directory.CreateTempSubdirectory("pelly-tp-");
        try
        {
            var csproj = Path.Combine(dir.FullName, "Legacy.csproj");
            File.WriteAllText(csproj,
                "<Project ToolsVersion=\"15.0\"><PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup></Project>");

            var check = TargetingPackCheck.Run(
                new FileSystem(), OperatingSystem.IsWindows, Environment.GetEnvironmentVariable("ProgramFiles(x86)"), csproj);

            Assert.Equal(DoctorStatus.Ok, check.Status);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
