using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Impact;

namespace PellyCoder.Core.Tests.Doctor;

public sealed class SemanticCheckTests : IDisposable
{
    private readonly string _baseDir = Directory.CreateTempSubdirectory("pelly-semcheck-").FullName;
    private readonly MockFileSystem _fs = new();

    public void Dispose() => Directory.Delete(_baseDir, recursive: true);

    private sealed class FakeLoader(SemanticFailure? failure = null) : ISemanticLoader
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> Projects { get; private set; } = [];

        public Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct)
        {
            Calls++;
            Projects = projects;
            return Task.FromResult(new SemanticLoad(null, failure, null));
        }
    }

    private void ShipBuildHost()
    {
        foreach (var folder in new[] { "BuildHost-net472", "BuildHost-netcore" })
        {
            Directory.CreateDirectory(Path.Combine(_baseDir, folder));
            File.WriteAllText(Path.Combine(_baseDir, folder, "host.dll"), "x");
        }
    }

    private static string Project => Path.Combine(OperatingSystem.IsWindows() ? @"C:\ws" : "/ws", "App.csproj");

    [Fact]
    public async Task Missing_build_host_is_Fail()
    {
        var loader = new FakeLoader();

        var row = await SemanticCheck.RunAsync(_fs, loader, _baseDir, Project, TestContext.Current.CancellationToken);

        Assert.Equal(SemanticCheck.Name, row.Name);
        Assert.Equal(DoctorStatus.Fail, row.Status);
        Assert.Equal(OfflineSelfTest.BuildHostRow(_baseDir).Action, row.Action);
        Assert.Equal(0, loader.Calls);
        Assert.Equal("missing", row.Facts!["buildHost"]);
    }

    [Fact]
    public async Task No_project_is_Ok_not_checked()
    {
        ShipBuildHost();
        var loader = new FakeLoader();

        var row = await SemanticCheck.RunAsync(_fs, loader, _baseDir, null, TestContext.Current.CancellationToken);

        Assert.Equal(DoctorStatus.Ok, row.Status);
        Assert.Equal("보조 프로세스 있음 (--project를 주면 불러오기까지 확인합니다)", row.Detail);
        Assert.Equal("notChecked", row.Facts!["semantic"]);
        Assert.Equal("present", row.Facts["buildHost"]);
        Assert.Equal(0, loader.Calls);
    }

    [Fact]
    public async Task Healthy_project_is_Ok()
    {
        ShipBuildHost();
        var loader = new FakeLoader();

        var row = await SemanticCheck.RunAsync(_fs, loader, _baseDir, Project, TestContext.Current.CancellationToken);

        Assert.Equal(DoctorStatus.Ok, row.Status);
        Assert.Equal("ok", row.Facts!["semantic"]);
        Assert.DoesNotContain("failure", row.Facts.Keys);
        Assert.Equal(1, loader.Calls);
        Assert.Equal([Path.GetFullPath(Project)], loader.Projects);
    }

    [Fact]
    public async Task Failure_is_Warn_with_fallback_message()
    {
        ShipBuildHost();
        var loader = new FakeLoader(new SemanticFailure(SemanticFailureKind.CompileErrors, Count: 3));

        var row = await SemanticCheck.RunAsync(_fs, loader, _baseDir, Project, TestContext.Current.CancellationToken);

        Assert.Equal(DoctorStatus.Warn, row.Status);
        Assert.Equal("이름 기준으로 대신합니다: 컴파일 오류 3건", row.Detail);
        Assert.Equal("fallback", row.Facts!["semantic"]);
        Assert.Equal("CompileErrors", row.Facts["failure"]);
    }
}
