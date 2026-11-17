using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using PellyCode.Core.Impact;
using PellyCode.McpServer.Impact;

namespace PellyCode.McpServer.Tests;

/// <summary>A temp git repository; <see cref="CreateModern"/> copies eval/corpus/Modern into &lt;root&gt;/Modern.</summary>
internal static class TempRepo
{
    public static string Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pelly-impact-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        // macOS: /var is a link to /private/var, and MSBuild hands back the resolved spelling.
        if (OperatingSystem.IsMacOS() && dir.StartsWith("/var/", StringComparison.Ordinal)) dir = "/private" + dir;
        Run(dir, "git", "init", "-q");
        Run(dir, "git", "config", "user.name", "t");
        Run(dir, "git", "config", "user.email", "t@example.com");
        Run(dir, "git", "config", "commit.gpgsign", "false");
        Run(dir, "git", "config", "core.autocrlf", "false");
        return dir;
    }

    public static string CreateModern(bool restore)
    {
        var dir = Create();
        Copy(new DirectoryInfo(ModernSource()), new DirectoryInfo(Path.Combine(dir, "Modern")));
        Commit(dir);
        if (restore)
            Run(dir, "dotnet", "restore", Path.Combine(dir, "Modern", "Shop.Api.Tests", "Shop.Api.Tests.csproj"), "--nologo");
        return dir;
    }

    public static void Commit(string dir)
    {
        Run(dir, "git", "add", "-A");
        Run(dir, "git", "commit", "-q", "-m", "c");
    }

    public static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string ModernSource()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "eval", "corpus", "Modern");
            if (Directory.Exists(p)) return p;
        }
        throw new DirectoryNotFoundException("eval/corpus/Modern");
    }

    private static void Copy(DirectoryInfo from, DirectoryInfo to)
    {
        to.Create();
        foreach (var f in from.GetFiles()) f.CopyTo(Path.Combine(to.FullName, f.Name));
        foreach (var d in from.GetDirectories())
            if (d.Name is not ("bin" or "obj" or "TestResults")) Copy(d, new DirectoryInfo(Path.Combine(to.FullName, d.Name)));
    }

    private static void Run(string dir, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{file} {string.Join(' ', args)} 실패:\n{output}{err.Result}");
    }
}

/// <summary>A restored Modern copy shared by the read-only Roslyn tests.</summary>
public sealed class RestoredModernFixture : IDisposable
{
    public string Root { get; } = TempRepo.CreateModern(restore: true);

    public void Dispose() => TempRepo.Delete(Root);
}

internal sealed class FailingLoader : ISemanticLoader
{
    public Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct) =>
        Task.FromResult(new SemanticLoad(null, new SemanticFailure(SemanticFailureKind.LoadFailed), null));
}

internal sealed class CountingLoader(ISemanticLoader inner) : ISemanticLoader
{
    public int Loads { get; private set; }

    public Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct)
    {
        Loads++;
        return inner.LoadAsync(projects, ct);
    }
}

/// <summary>Loads one AdhocWorkspace project holding every .cs file beside the first project file.</summary>
internal sealed class AdhocLoader : ISemanticLoader
{
    public Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct)
    {
        var ws = new AdhocWorkspace();
        var project = ws.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp,
            filePath: projects[0]));
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(projects[0])!, "*.cs"))
            project = project.AddDocument(Path.GetFileName(file), SourceText.From(File.ReadAllText(file)), filePath: file).Project;
        return Task.FromResult(new SemanticLoad(project.Solution, null, ws));
    }
}

public sealed class ImpactCsTests(RestoredModernFixture fx) : IClassFixture<RestoredModernFixture>
{
    private static readonly FileSystem Fs = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServerContext Context(string root) =>
        ServerContext.TryCreate(Fs, root, out var error) ?? throw new InvalidOperationException(error);

    private static ImpactService Service(string root, ISemanticLoader loader) => new(Fs, Context(root), loader);

    private static string[] Lines(string text) => text.Split('\n');

    [Fact]
    public async Task Cs_ByLine_ListsCallers()
    {
        var text = await Service(fx.Root, new MsBuildSemanticLoader(Fs))
            .FindAsync("Modern/Shop.Api/Services/OrderService.cs", 40, null, Ct);

        var lines = Lines(text);
        Assert.Equal("대상: OrderService.CancelAsync (Modern/Shop.Api/Services/OrderService.cs:33)", lines[0]);
        Assert.True(text.Contains("(Roslyn 의미 분석)", StringComparison.Ordinal), text);
        Assert.Contains("- Modern/Shop.Api/Controllers/OrdersController.cs:46 OrdersController.Cancel — 호출", lines);
        Assert.Contains("- Modern/Shop.Api.Tests/OrderServiceTests.cs:22 OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels — 호출", lines);
        Assert.Contains("- Modern/Shop.Api/Services/IOrderService.cs:9 IOrderService.CancelAsync — 기반 멤버", lines);
    }

    [Fact]
    public async Task Cs_ByName()
    {
        // Absolute path, and a Class.Member name.
        var text = await Service(fx.Root, new MsBuildSemanticLoader(Fs))
            .FindAsync(Path.Combine(fx.Root, "Modern", "Shop.Api", "Services", "OrderService.cs"), null, "OrderService.CalculateRefundAsync", Ct);

        var lines = Lines(text);
        Assert.StartsWith("대상: OrderService.CalculateRefundAsync (Modern/Shop.Api/Services/OrderService.cs:", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("- Modern/Shop.Api/Services/OrderService.cs:", StringComparison.Ordinal)
            && l.EndsWith(" OrderService.CancelAsync — 호출", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cs_NoLineNoName_Error()
    {
        var text = await Service(fx.Root, new FailingLoader()).FindAsync("Modern/Shop.Api/Services/OrderService.cs", null, null, Ct);

        Assert.Equal("line이나 name을 주세요", text);
    }

    [Fact]
    public async Task Cs_NameFallback_NotesIt()
    {
        var text = await Service(fx.Root, new FailingLoader()).FindAsync("Modern/Shop.Api/Services/OrderService.cs", null, "CancelAsync", Ct);

        var lines = Lines(text);
        Assert.Equal("대상: OrderService.CancelAsync (Modern/Shop.Api/Services/OrderService.cs:33)", lines[0]);
        Assert.StartsWith("이름 기준으로 찾았습니다(빠진 곳이 있을 수 있음)", lines[1]);
        Assert.Contains("- Modern/Shop.Api/Controllers/OrdersController.cs:46 OrdersController.Cancel — 호출", lines);
    }

    [Fact]
    public async Task Cs_Over50_Truncated()
    {
        var root = TempRepo.Create();
        try
        {
            var sb = new StringBuilder("class Target { public void Go() { } }\nclass Callers {\n");
            for (var i = 0; i < 60; i++) sb.Append($"  void C{i}(Target t) {{ t.Go(); }}\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(root, "A.cs"), sb.ToString());

            var text = await Service(root, new FailingLoader()).FindAsync("A.cs", 1, null, Ct);

            var lines = Lines(text);
            Assert.Contains("부르는 곳 60곳", text);
            Assert.Equal(50, lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
            Assert.Contains("외 10곳", lines);
        }
        finally { TempRepo.Delete(root); }
    }

    [Fact]
    public async Task Unsupported_Extension()
    {
        var text = await Service(fx.Root, new FailingLoader()).FindAsync("Modern/Modern.sln", 1, null, Ct);

        Assert.StartsWith("지원하지 않는 파일 형식", text);
    }

    [Fact]
    public async Task CachingLoader_SecondCall_NoReload()
    {
        var root = TempRepo.Create();
        try
        {
            var csproj = Path.Combine(root, "P.csproj");
            File.WriteAllText(csproj, "<Project />");
            File.WriteAllText(Path.Combine(root, "A.cs"), "class A { }");
            var inner = new CountingLoader(new AdhocLoader());
            var loader = new CachingSemanticLoader(inner, Fs);

            var first = await loader.LoadAsync([csproj], Ct);
            var second = await loader.LoadAsync([csproj], Ct);

            Assert.Equal(1, inner.Loads);
            Assert.NotNull(second.Solution);
            first.Dispose(); // the caller's using must not dispose the cached workspace
            second.Dispose();
            Assert.NotNull((await loader.LoadAsync([csproj], Ct)).Solution);
            Assert.Equal(1, inner.Loads);
        }
        finally { TempRepo.Delete(root); }
    }

    [Fact]
    public async Task CachingLoader_CsprojChanged_Reloads()
    {
        var root = TempRepo.Create();
        try
        {
            var csproj = Path.Combine(root, "P.csproj");
            File.WriteAllText(csproj, "<Project />");
            File.WriteAllText(Path.Combine(root, "A.cs"), "class A { }");
            var inner = new CountingLoader(new AdhocLoader());
            var loader = new CachingSemanticLoader(inner, Fs);

            await loader.LoadAsync([csproj], Ct);
            File.SetLastWriteTimeUtc(csproj, File.GetLastWriteTimeUtc(csproj).AddSeconds(5));
            await loader.LoadAsync([csproj], Ct);

            Assert.Equal(2, inner.Loads);
        }
        finally { TempRepo.Delete(root); }
    }

    [Fact]
    public async Task CachingLoader_EditedCs_SeesNewCall()
    {
        var root = TempRepo.CreateModern(restore: true);
        try
        {
            var inner = new CountingLoader(new MsBuildSemanticLoader(Fs));
            var service = Service(root, new CachingSemanticLoader(inner, Fs));
            const string seed = "Modern/Shop.Api/Services/OrderService.cs";

            var before = await service.FindAsync(seed, null, "CancelAsync", Ct);
            Assert.DoesNotContain("Cancel2", before);

            var controller = Path.Combine(root, "Modern", "Shop.Api", "Controllers", "OrdersController.cs");
            var source = File.ReadAllText(controller).Replace(
                "        [HttpGet]\n",
                "        public Task Cancel2() => _orderService.CancelAsync(1, \"x\");\n\n        [HttpGet]\n");
            File.WriteAllText(controller, source);
            File.SetLastWriteTimeUtc(controller, File.GetLastWriteTimeUtc(controller).AddSeconds(5));

            var after = await service.FindAsync(seed, null, "CancelAsync", Ct);

            Assert.True(after.Contains("(Roslyn 의미 분석)", StringComparison.Ordinal), after);
            Assert.Contains(Lines(after), l => l.StartsWith("- Modern/Shop.Api/Controllers/OrdersController.cs:", StringComparison.Ordinal)
                && l.EndsWith(" OrdersController.Cancel2 — 호출", StringComparison.Ordinal));
            Assert.Equal(1, inner.Loads);
        }
        finally { TempRepo.Delete(root); }
    }
}
