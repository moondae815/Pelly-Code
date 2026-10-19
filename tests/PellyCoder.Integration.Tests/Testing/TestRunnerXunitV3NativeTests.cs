using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Testing;

namespace PellyCoder.Integration.Tests.Testing;

/// <summary>
/// A real project on the plain xunit.v3 package (in-process console runner, not Microsoft.Testing.Platform) end to end: it must run
/// with <c>-trx</c> and give per-test results. The project lives in a temp folder outside the repo (no Central Package Management).
/// </summary>
public class TestRunnerXunitV3NativeTests
{
    // xunit.v3 3.2.2 and 4.0.2 were both measured to reject -result-trx and accept -trx on 2026-10-19 (SDK 10.0.302).
    private const string XunitV3Version = "3.2.2";

    [Fact]
    public async Task PlainXunitV3_RealProject_PerTestResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = Path.Combine(Path.GetTempPath(), "pelly-xv3-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(created);
        var root = TestRunnerSdkTests.RealPath(created);
        try
        {
            Write(root, "Calc.Tests/Calc.Tests.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
                  <ItemGroup><PackageReference Include="xunit.v3" Version="{XunitV3Version}" /></ItemGroup>
                </Project>
                """);
            Write(root, "Calc.Tests/CalculatorTests.cs", """
                using Xunit;
                namespace Calc.Tests;
                public class CalculatorTests
                {
                    [Fact] public void Add_Small() => Assert.Equal(3, 1 + 2);
                    [Fact] public void Add_Wrong() => Assert.Equal(4, 1 + 2);
                }
                """);

            var csproj = Path.Combine(root, "Calc.Tests", "Calc.Tests.csproj");
            var processes = new SystemProcessRunner(TimeProvider.System);
            var restore = await processes.RunAsync(new ProcessSpec("dotnet", ["restore", csproj, "--nologo"], root, TimeSpan.FromMinutes(5)), ct);
            Assert.True(restore.ExitCode == 0, restore.StdOut + restore.StdErr);

            var trx = Path.Combine(root, ".pelly", "test.trx");
            var run = await new TestRunner(processes, new BuildOptions(), new TestOptions())
                .RunAsync(new BuildTool(BuildToolKind.Dotnet, "dotnet"), csproj, root, trx, ct);

            Assert.Null(run.Error);
            Assert.Empty(run.BuildErrors);
            Assert.True(run.PerTestResults);
            var results = run.Results.ToDictionary(r => r.FullName, r => r.Passed);
            Assert.Equal(2, results.Count);
            Assert.True(results["Calc.Tests.CalculatorTests.Add_Small"]);
            Assert.False(results["Calc.Tests.CalculatorTests.Add_Wrong"]);
        }
        finally
        {
            try { Directory.Delete(created, recursive: true); }
            catch (IOException) { /* best effort: MSBuild node reuse may still hold files */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
