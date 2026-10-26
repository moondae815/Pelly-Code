using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Cli.Commands;

namespace PellyCoder.Cli.Tests;

public class RunArgumentsTests
{
    [Fact]
    public void ValidateCs_LineWithMethod()
    {
        var errors = RunArguments.ValidateCs(new CsSettings { File = "A.cs", Line = 3, Method = "M" });
        Assert.Equal(["--line은 --class/--method와 함께 쓸 수 없습니다"], errors);
    }

    [Fact]
    public void ValidateCs_ExpectBreakWithoutVerify()
    {
        var errors = RunArguments.ValidateCs(new CsSettings { File = "A.cs", ExpectBreak = ["A.B.C"] });
        Assert.Equal(["--expect-break는 --verify-test와 함께 써야 합니다"], errors);
    }

    [Fact]
    public void ValidateCs_Valid_Empty() =>
        Assert.Empty(RunArguments.ValidateCs(new CsSettings { File = "A.cs", Line = 3, VerifyTest = ["A.B.C"] }));

    [Fact]
    public void ValidateSql_LineWithObject()
    {
        var errors = RunArguments.ValidateSql(new SqlSettings { File = "A.sql", Line = 3, Object = "dbo.P" });
        Assert.Equal(["--line은 --object와 함께 쓸 수 없습니다"], errors);
    }

    [Fact]
    public void PrepareCs_MissingFile()
    {
        var fs = new MockFileSystem();
        var resolver = new OptionsResolver(fs, new ConfigSources(null, new Dictionary<string, string?>()));
        var (run, errors) = RunArguments.PrepareCs(new CsSettings { File = "/w/Missing.cs" }, fs, resolver);
        Assert.Null(run);
        Assert.StartsWith("파일을 찾을 수 없습니다: ", errors[0]);
    }

    [Fact]
    public void CsRequest_CarriesVerifyTests()
    {
        var request = RunArguments.CsRequest(new CsSettings { File = "A.cs", VerifyTest = ["A.B.C", "A.B.C"] }, "/w/A.cs", "do it");
        Assert.Equal(["A.B.C"], request.VerifyTests);
        Assert.Null(request.ExpectBreaks);
    }
}
