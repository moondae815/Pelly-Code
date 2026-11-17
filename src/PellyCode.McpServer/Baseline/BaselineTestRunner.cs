using System.IO.Abstractions;
using System.Xml;
using PellyCode.Core.Builds;
using PellyCode.Core.Testing;

namespace PellyCode.McpServer.Baseline;

/// <summary>
/// Runs a baseline test project with Core's <see cref="TestRunner"/> after choosing its build tool like GroupTester does.
/// A project that cannot be run here (unreadable, legacy off Windows, no MSBuild) comes back as a run with an <c>Error</c>.
/// </summary>
public sealed class BaselineTestRunner(IFileSystem fs, TestRunner runner, BuildToolResolver tools, Func<bool> isWindows) : IBaselineTestRunner
{
    public async Task<TestRun> RunAsync(string testProject, string workDir, string trxPath, CancellationToken ct)
    {
        ProjectStyle style;
        try
        {
            style = ProjectStyleDetector.Detect(fs, testProject);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return Failed($"프로젝트 파일을 읽을 수 없습니다 ({ex.Message})");
        }
        if (style == ProjectStyle.None) return Failed("프로젝트 형식을 알 수 없습니다");
        if (style == ProjectStyle.Legacy && !isWindows()) return Failed("레거시 테스트는 Windows에서만 실행합니다");

        var resolution = await tools.ResolveAsync(style, ct);
        if (resolution.Tool is not { } tool) return Failed(resolution.Error!);
        return await runner.RunAsync(tool, testProject, workDir, trxPath, ct);
    }

    private static TestRun Failed(string error) => new([], [], -1, false, false, error);
}
