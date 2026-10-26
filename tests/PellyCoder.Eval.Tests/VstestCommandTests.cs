using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

/// <summary>
/// A case's legacy <c>test.command</c> may name <c>vstest.console.exe</c> bare. Off a Developer prompt it is not on PATH
/// (the CI Windows runner), so the bare name is replaced with the Visual Studio copy vswhere finds.
/// </summary>
public sealed class VstestCommandTests : IDisposable
{
    private const string Command = @"vstest.console.exe {dll} /logger:trx;LogFileName={trx}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-vstest-cmd-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FileSystem _fs = new();

    public VstestCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string ProgramFilesX86 => Path.Combine(_root, "pf86");
    private string Vswhere => Path.Combine(ProgramFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private sealed class FakeProcesses(Func<ProcessSpec, ProcessResult> answer) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            return Task.FromResult(answer(spec));
        }
    }

    private static ProcessResult Output(string stdout, int exit = 0) =>
        new(exit, stdout, "", Encoding.UTF8.GetBytes(stdout), TimeSpan.FromMilliseconds(10), false);

    private Task<string> Resolve(string command, IProcessRunner processes, string? path) =>
        VstestCommand.ResolveAsync(command, processes, _fs, path, ProgramFilesX86, TestContext.Current.CancellationToken);

    [Fact]
    public async Task BareName_NotOnPath_UsesVswhereCopy()
    {
        Touch("pf86", "Microsoft Visual Studio", "Installer", "vswhere.exe");
        var found = Touch("vs", "Common7", "IDE", "Extensions", "TestPlatform", "vstest.console.exe");
        var processes = new FakeProcesses(_ => Output(found + "\r\n"));

        var resolved = await Resolve(Command, processes, Path.Combine(_root, "empty"));

        Assert.Equal($"\"{found}\" {{dll}} /logger:trx;LogFileName={{trx}}", resolved);
        var call = Assert.Single(processes.Calls);
        Assert.Equal(Vswhere, call.FileName);
        Assert.Contains("-find", call.Arguments);
        Assert.Contains(@"**\TestPlatform\vstest.console.exe", call.Arguments);
    }

    [Fact]
    public async Task BareName_OnPath_Unchanged_NoVswhere()
    {
        var dir = Path.GetDirectoryName(Touch("bin", "vstest.console.exe"))!;
        var processes = new FakeProcesses(_ => throw new InvalidOperationException("vswhere must not run"));

        Assert.Equal(Command, await Resolve(Command, processes, dir));
        Assert.Empty(processes.Calls);
    }

    [Fact]
    public async Task ExplicitPath_Unchanged()
    {
        var command = @"""C:\VS\vstest.console.exe"" {dll}";
        var processes = new FakeProcesses(_ => throw new InvalidOperationException("vswhere must not run"));

        Assert.Equal(command, await Resolve(command, processes, null));
    }

    [Fact]
    public async Task OtherTool_Unchanged()
    {
        var processes = new FakeProcesses(_ => throw new InvalidOperationException("vswhere must not run"));

        Assert.Equal("nunit3-console.exe {dll}", await Resolve("nunit3-console.exe {dll}", processes, null));
    }

    [Fact]
    public async Task NoVswhere_Unchanged()
    {
        var processes = new FakeProcesses(_ => throw new InvalidOperationException("vswhere must not run"));

        Assert.Equal(Command, await Resolve(Command, processes, Path.Combine(_root, "empty")));
    }

    [Fact]
    public async Task VswhereFindsNothing_Unchanged()
    {
        Touch("pf86", "Microsoft Visual Studio", "Installer", "vswhere.exe");
        var processes = new FakeProcesses(_ => Output(""));

        Assert.Equal(Command, await Resolve(Command, processes, null));
    }
}
