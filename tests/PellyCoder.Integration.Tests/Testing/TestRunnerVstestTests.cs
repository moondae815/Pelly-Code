using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Testing;

namespace PellyCoder.Integration.Tests.Testing;

/// <summary>
/// The VSTest path (<c>dotnet test --logger trx</c>) end to end against a real xunit v2 project. The project lives in a temp
/// folder outside the repo, so the repo's Central Package Management does not apply and the versions below are its own.
/// </summary>
public class TestRunnerVstestTests
{
    // Latest stable versions on nuget.org on 2026-10-18 within the brief's lines (xunit 2.9.x, Test.Sdk 17.x, runner 2.8.x).
    // Runner 2.8.2 was measured to discover and run net10.0 tests under SDK 10.0.302.
    private const string XunitVersion = "2.9.3";
    private const string TestSdkVersion = "17.14.1";
    private const string RunnerVersion = "2.8.2";

    [Fact]
    public async Task Vstest_RealProject_PerTestResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = Path.Combine(Path.GetTempPath(), "pelly-vstest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(created);
        // Link-free root: through the macOS /var -> /private/var link the restore skips the ProjectReference (see TestRunnerSdkTests).
        var root = TestRunnerSdkTests.RealPath(created);
        try
        {
            Write(root, "Calc/Calc.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            Write(root, "Calc/Calculator.cs", "namespace Calc; public static class Calculator { public static int Add(int a, int b) => a + b; }");
            Write(root, "Calc.Tests/Calc.Tests.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="{TestSdkVersion}" />
                    <PackageReference Include="xunit" Version="{XunitVersion}" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="{RunnerVersion}" />
                  </ItemGroup>
                  <ItemGroup><ProjectReference Include="../Calc/Calc.csproj" /></ItemGroup>
                </Project>
                """);
            Write(root, "Calc.Tests/CalculatorTests.cs", """
                using Xunit;
                namespace Calc.Tests;
                public class CalculatorTests
                {
                    [Fact] public void Add_Small() => Assert.Equal(3, Calculator.Add(1, 2));
                    [Fact] public void Add_Zero() => Assert.Equal(5, Calculator.Add(5, 0));
                    [Fact] public void Add_Wrong() => Assert.Equal(4, Calculator.Add(1, 2));
                }
                """);

            var csproj = Path.Combine(root, "Calc.Tests", "Calc.Tests.csproj");
            Assert.Equal(TestRunner.TestProjectKind.VsTest, TestRunner.Kind(csproj));
            var processes = new SystemProcessRunner(TimeProvider.System);
            var restore = await processes.RunAsync(new ProcessSpec("dotnet", ["restore", Path.Combine("Calc.Tests", "Calc.Tests.csproj"), "--nologo"], root, TimeSpan.FromMinutes(5)), ct);
            Assert.True(restore.ExitCode == 0, restore.StdOut + restore.StdErr);

            var trx = Path.Combine(root, ".pelly", "test.trx");
            var run = await new TestRunner(processes, new BuildOptions(), new TestOptions())
                .RunAsync(new BuildTool(BuildToolKind.Dotnet, "dotnet"), csproj, root, trx, ct);

            Assert.Null(run.Error);
            Assert.Empty(run.BuildErrors);
            Assert.True(run.PerTestResults);
            Assert.NotEqual(0, run.ExitCode);
            var results = run.Results.ToDictionary(r => r.FullName, r => r.Passed);
            Assert.Equal(3, results.Count);
            Assert.True(results["Calc.Tests.CalculatorTests.Add_Small"]);
            Assert.True(results["Calc.Tests.CalculatorTests.Add_Zero"]);
            Assert.False(results["Calc.Tests.CalculatorTests.Add_Wrong"]);
            Assert.StartsWith("Assert.Equal() Failure", run.Results.Single(r => !r.Passed).Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(created, recursive: true); }
            catch (IOException) { /* best effort: MSBuild node reuse may still hold files */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// What a VSTest host killed mid-run leaves behind (measured 2026-10-19): a TRX with run outcome <c>Failed</c> — the same as a
    /// normal failing run — and fewer results than the baseline. TestStep therefore needs the result count, not only the outcome.
    /// </summary>
    [Fact]
    public async Task Vstest_HostKilledByFailFast_PartialReportWithFailedOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = Path.Combine(Path.GetTempPath(), "pelly-vstest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(created);
        var root = TestRunnerSdkTests.RealPath(created);
        try
        {
            Write(root, "Crash.Tests/Crash.Tests.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="{TestSdkVersion}" />
                    <PackageReference Include="xunit" Version="{XunitVersion}" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="{RunnerVersion}" />
                  </ItemGroup>
                </Project>
                """);
            Write(root, "Crash.Tests/CrashTests.cs", """
                using Xunit;
                namespace Crash.Tests;
                public class CrashTests
                {
                    [Fact] public void Pass1() { }
                    [Fact] public void Fail1() => Assert.Equal(1, 2);
                    [Fact] public void Crash() => System.Environment.FailFast("pelly-test-crash");
                    [Fact] public void Pass2() { }
                }
                """);

            var csproj = Path.Combine(root, "Crash.Tests", "Crash.Tests.csproj");
            var processes = new SystemProcessRunner(TimeProvider.System);
            var restore = await processes.RunAsync(new ProcessSpec("dotnet", ["restore", Path.Combine("Crash.Tests", "Crash.Tests.csproj"), "--nologo"], root, TimeSpan.FromMinutes(5)), ct);
            Assert.True(restore.ExitCode == 0, restore.StdOut + restore.StdErr);

            var trx = Path.Combine(root, ".pelly", "test.trx");
            var run = await new TestRunner(processes, new BuildOptions(), new TestOptions())
                .RunAsync(new BuildTool(BuildToolKind.Dotnet, "dotnet"), csproj, root, trx, ct);

            Assert.Null(run.Error);
            Assert.True(run.PerTestResults);
            Assert.NotEqual(0, run.ExitCode);
            Assert.False(run.RunAborted);           // outcome="Failed", indistinguishable from a normal failing run
            Assert.True(run.Results.Count < 4);     // but the report is short: the killed host never reported every test
            Assert.DoesNotContain(run.Results, r => r.FullName == "Crash.Tests.CrashTests.Crash");
        }
        finally
        {
            try { Directory.Delete(created, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
