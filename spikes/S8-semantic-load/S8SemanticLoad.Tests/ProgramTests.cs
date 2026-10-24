using S8SemanticLoad;

public class ProgramTests
{
    [Fact] public async Task Usage_error_exits_2() => Assert.Equal(2, await Program.RunAsync([]));

    [Fact] public async Task Missing_path_exits_2() => Assert.Equal(2, await Program.RunAsync(["load", "/nope/none.sln"]));

    [Fact] public async Task Unknown_option_exits_2() =>
        Assert.Equal(2, await Program.RunAsync(["load", Path.Combine(TestPaths.Corpus, "Modern", "Modern.sln"), "--bogus"]));
}

public class ProgramExitTests
{
    [Fact] public void Missing_build_host_exception_exits_3() =>
        Assert.Equal(3, Program.ExitCodeFor(new Exception("Build host not found at /x/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll")));

    [Fact] public void Other_exception_exits_1() => Assert.Equal(1, Program.ExitCodeFor(new InvalidOperationException("boom")));

    // 한국어 Windows에서는 Roslyn·MSBuild 메시지가 번역되어 분류 규칙(영어 낱말)이 모두 「기타」가 된다.
    [Fact] public void Messages_are_forced_to_english()
    {
        var before = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ko-KR");
            Program.ForceEnglishMessages();
            Assert.Equal("", System.Globalization.CultureInfo.CurrentUICulture.Name);
            Assert.Equal("1033", Environment.GetEnvironmentVariable("VSLANG"));
            Assert.Equal("en", Environment.GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE"));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = before; }
    }
}

public class ArmCCommandTests
{
    [Fact] public async Task Arm_c_writes_rows_to_out_file()
    {
        var file = Path.Combine(Path.GetTempPath(), "s8-armc-" + Guid.NewGuid().ToString("N") + ".json");
        var code = await Program.RunAsync(["arm-c", "--repo", TestPaths.RepoRoot, "--out", file]);
        Assert.Equal(OperatingSystem.IsWindows() ? 0 : 3, code);
        Assert.Contains("a3-rename-getorder", File.ReadAllText(file));
        File.Delete(file);
    }

    [Fact] public async Task Arm_c_unknown_option_exits_2() => Assert.Equal(2, await Program.RunAsync(["arm-c", "--bogus"]));
}
