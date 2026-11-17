using System.IO.Abstractions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PellyCode.Core.Builds;
using PellyCode.Core.Impact;
using PellyCode.Core.Processes;
using PellyCode.Core.Specs;
using PellyCode.Core.Testing;
using PellyCode.McpServer;
using PellyCode.McpServer.Baseline;
using PellyCode.McpServer.Git;
using PellyCode.McpServer.Impact;
using PellyCode.McpServer.Tools;
using PellyCode.McpServer.Verify;

// --root <folder> (default: current folder) and --log-file <path> are ours; the rest goes to the host.
var root = ArgValue(args, "--root") ?? Directory.GetCurrentDirectory();
var logFile = ArgValue(args, "--log-file");

var builder = Host.CreateApplicationBuilder(args);

// stdout is for MCP messages only: console logs go to stderr, default providers are removed.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
if (logFile is not null) builder.Logging.AddProvider(new FileLoggerProvider(logFile));

var context = ServerContext.TryCreate(new FileSystem(), root, out var error);
builder.Services.AddSingleton(new ServerState(context, error));
// One loader for the server's life: it keeps each project bundle's Roslyn solution between pelly_impact calls.
var fs = new FileSystem();
builder.Services.AddSingleton<IFileSystem>(fs);
builder.Services.AddSingleton<ISemanticLoader>(new CachingSemanticLoader(new MsBuildSemanticLoader(fs), fs));

// The HEAD baseline (design §11): prebuilt in the background in the mirror outside the workspace, outside the tool lock.
if (context is not null)
{
    var processes = new SystemProcessRunner(TimeProvider.System);
    var options = context.Options;
    var tools = new BuildToolResolver(fs, processes, options.Build);
    var verifier = new GroupVerifier(fs, new BuildVerifier(processes, options.Build), tools, OperatingSystem.IsWindows);
    var tests = new BaselineTestRunner(fs, new TestRunner(processes, options.Build, options.Test), tools, OperatingSystem.IsWindows);
    builder.Services.AddSingleton(sp => new BaselineService(fs, context.Root, MirrorPaths.For(context.Root),
        new GitRepo(processes, context.Root), verifier, tests, options.Test, TimeProvider.System,
        sp.GetRequiredService<ILogger<BaselineService>>()));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BaselineService>());
    // The live tree is built and tested with the same verifier and test runner, so both sides report paths the same way.
    builder.Services.AddSingleton(sp => new VerifyService(fs, context, new GitRepo(processes, context.Root),
        sp.GetRequiredService<BaselineService>(), verifier, tests, VerifyService.DefaultWaitLimit));
}

var version = typeof(ServerContext).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "pellycode-mcp-server", Version = version })
    .WithStdioServerTransport()
    .WithTools<PellyTools>();

await builder.Build().RunAsync();
return 0;

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
