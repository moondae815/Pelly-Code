using PellyCoder.Core.Builds;

namespace PellyCoder.Core.Tests.Builds;

public class BuildErrorParserTests
{
    /// <summary>
    /// 녹화 파일은 섹션마다 첫 줄에 "# root: &lt;절대 경로&gt;"를 남기고 그 뒤에 dotnet build 출력이 이어진다.
    /// 다음 "# root:" 줄이 나오면 새 섹션이다. "#" 줄 자체는 파서 정규식에 걸리지 않아 무시된다.
    /// </summary>
    private static List<(string Root, string Output)> LoadSections()
    {
        var sections = new List<(string, List<string>)>();
        foreach (var line in File.ReadAllLines(Fixture("dotnet-sdk-errors.txt")))
        {
            if (line.StartsWith("# root: ", StringComparison.Ordinal))
                sections.Add((line["# root: ".Length..].Trim(), []));
            else
                sections[^1].Item2.Add(line);
        }

        return sections.Select(s => (s.Item1, string.Join('\n', s.Item2))).ToList();
    }

    private static string Fixture(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "tests", "fixtures", "build", name);
            if (File.Exists(p)) return p;
        }

        throw new FileNotFoundException(name);
    }

    [Fact]
    public void DotnetRecorded_ParsesCS0103AndCS0029_RelativePaths()
    {
        var (root, output) = LoadSections()[0];
        var diags = BuildErrorParser.Parse(output, root, caseInsensitivePaths: false);

        Assert.Equal(2, diags.Count);
        Assert.All(diags, d =>
        {
            Assert.Equal("Shop.Api/Services/OrderService.cs", d.File);
            Assert.True(d.Line > 0);
            Assert.Equal("error", d.Severity);
        });
        Assert.Contains(diags, d => d.Code == "CS0103" && d.Line == 46 && d.Column == 13);
        Assert.Contains(diags, d => d.Code == "CS0029");
    }

    [Fact]
    public void KoreanPath_Parses()
    {
        var (root, output) = LoadSections()[1];
        Assert.Contains("한글폴더", root);
        var diags = BuildErrorParser.Parse(output, root, caseInsensitivePaths: false);

        Assert.Equal(2, diags.Count);
        Assert.All(diags, d => Assert.Equal("Shop.Api/Services/OrderService.cs", d.File));
    }

    [Fact]
    public void ProjectSuffix_IsStripped_FromMessage()
    {
        var (root, output) = LoadSections()[0];
        var d = BuildErrorParser.Parse(output, root, false).First(x => x.Code == "CS0103");

        Assert.Equal("The name 'undefinedVar' does not exist in the current context", d.Message);
        Assert.DoesNotContain("csproj", d.Message);
    }

    [Fact]
    public void Duplicates_AreRemoved_OrderKept()
    {
        const string output = """
            C:\w\a.cs(3,4): error CS1002: ; expected [C:\w\p.csproj]
            C:\w\b.cs(1,1): error CS0103: x [C:\w\p.csproj]
            C:\w\a.cs(3,4): error CS1002: ; expected [C:\w\q.csproj]
            """;
        var diags = BuildErrorParser.Parse(output, @"C:\w", true);

        Assert.Equal(["a.cs", "b.cs"], diags.Select(d => d.File));
    }

    [Fact]
    public void FilelessLines_HaveEmptyFileAndLineZero()
    {
        var diags = BuildErrorParser.Parse(Legacy(), @"C:\W", true);

        var msb = Assert.Single(diags, d => d.Code == "MSB1009");
        Assert.Equal(("", 0, 0, "error"), (msb.File, msb.Line, msb.Column, msb.Severity));
        Assert.Equal("Project file does not exist.", msb.Message);
        var csc = Assert.Single(diags, d => d.Code == "CS2001");
        Assert.Equal(("", 0, 0), (csc.File, csc.Line, csc.Column));
        Assert.DoesNotContain("csproj", csc.Message);
    }

    [Fact]
    public void Warnings_HaveWarningSeverity()
    {
        var diags = BuildErrorParser.Parse(Legacy(), @"C:\W", true);

        var w = Assert.Single(diags, d => d.Code == "CS0168");
        Assert.Equal("warning", w.Severity);
        Assert.Equal((30, 9), (w.Line, w.Column));
    }

    [Fact]
    public void WindowsPaths_CaseInsensitive_DedupeAndRelativize()
    {
        const string output = """
            C:\W\Src\A.cs(1,2): error CS0103: x [C:\W\p.csproj]
            c:\w\src\a.cs(1,2): error CS0103: x [C:\W\p.csproj]
            """;
        var diags = BuildErrorParser.Parse(output, @"C:\w", true);

        var d = Assert.Single(diags);
        Assert.Equal("Src/A.cs", d.File);

        // 대소문자를 구분하면 루트와 맞지 않아 절대 경로로 남고 둘은 다른 줄이다.
        Assert.Equal(2, BuildErrorParser.Parse(output, @"C:\w", false).Count);
        // 끝 구분자가 있는 루트도 같다.
        Assert.Equal("Src/A.cs", Assert.Single(BuildErrorParser.Parse(output, @"C:\w\", true)).File);
    }

    [Fact]
    public void PathOutsideRoot_StaysAbsolute()
    {
        var diags = BuildErrorParser.Parse(Legacy(), @"C:\W", true);

        var d = Assert.Single(diags, x => x.Code == "CS1002");
        Assert.Equal("D:/Other/Lib/Helper.cs", d.File);
        // 루트 이름을 접두사로만 공유하는 형제 폴더는 안쪽이 아니다.
        var sibling = BuildErrorParser.Parse(@"C:\Wx\a.cs(1,1): error CS0103: x", @"C:\W", true);
        Assert.Equal("C:/Wx/a.cs", Assert.Single(sibling).File);
    }

    [Fact]
    public void UnrelatedLines_AreIgnored()
    {
        const string output = """
            Build FAILED.
                0 Warning(s)
                2 Error(s)
            Time Elapsed 00:00:00.45
            # root: /x
            """;

        Assert.Empty(BuildErrorParser.Parse(output, "/x", false));
        Assert.Empty(BuildErrorParser.Parse("", "/x", false));
    }

    private static string Legacy() => File.ReadAllText(Fixture("msbuild-legacy-errors.txt"));
}
