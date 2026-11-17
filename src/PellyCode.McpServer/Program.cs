using System.IO.Abstractions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PellyCode.Core.Builds;
using PellyCode.Core.Impact;
using PellyCode.Core.Processes;
using PellyCode.Core.Specs;
using PellyCode.Core.Sql;
using PellyCode.Core.Testing;
using PellyCode.McpServer;
using PellyCode.McpServer.Baseline;
using PellyCode.McpServer.Git;
using PellyCode.McpServer.Impact;
using PellyCode.McpServer.Tools;
using PellyCode.McpServer.Verify;

// --root <folder> (default: current folder) and --log-file <path> are ours; the rest goes to the host.
var root = CommandLine.Value(args, CommandLine.Root) ?? Directory.GetCurrentDirectory();
var logFile = CommandLine.Value(args, CommandLine.LogFile);

var check = args.Contains(CommandLine.Check);
// The content root is the server's own folder, not the current folder (the user's project): an appsettings.json there must
// neither be loaded (a malformed one would stop the server) nor watched.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });

// stdout is for MCP messages only: console logs go to stderr, default providers are removed.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
if (logFile is not null) builder.Logging.AddProvider(new FileLoggerProvider(logFile));

var context = ServerContext.TryCreate(new FileSystem(), root, out var error);
// A startup failure also goes to the log file (and stderr), so a client that only shows "server failed" leaves a trace.
if (context is null)
{
    using var startupLog = LoggerFactory.Create(b =>
    {
        b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        if (logFile is not null) b.AddProvider(new FileLoggerProvider(logFile));
    });
    startupLog.CreateLogger("PellyCodeMcpServer").LogError("시작하지 못했습니다: {Error}", error);
}
builder.Services.AddSingleton(new ServerState(context, error));
// One loader for the server's life: it keeps each project bundle's Roslyn solution between pelly_impact calls.
var fs = new FileSystem();
builder.Services.AddSingleton<IFileSystem>(fs);
builder.Services.AddSingleton<ISemanticLoader>(new CachingSemanticLoader(new MsBuildSemanticLoader(fs), fs));

// The HEAD baseline (design §11): prebuilt in the background in the mirror outside the workspace, outside the tool lock.
var processes = new SystemProcessRunner(TimeProvider.System);
// One verifier and test runner for the whole server: the baseline and the live tree report paths the same way.
GroupVerifier? verifier = null;
BaselineTestRunner? tests = null;
BaselineService? MakeBaseline(ILogger<BaselineService> log) => context is null ? null
    : new BaselineService(fs, context.Root, MirrorPaths.For(context.Root), new GitRepo(processes, context.Root),
        verifier!, tests!, context.Options.Test, TimeProvider.System, log, new DotnetRestorer(fs, processes, context.Options.Build));
if (context is not null)
{
    var options = context.Options;
    var tools = new BuildToolResolver(fs, processes, options.Build);
    verifier = new GroupVerifier(fs, new BuildVerifier(processes, options.Build), tools, OperatingSystem.IsWindows);
    tests = new BaselineTestRunner(fs, new TestRunner(processes, options.Build, options.Test), tools, OperatingSystem.IsWindows);
}

// --check: a self-test on stdout; no MCP, no baseline build (only the cache on disk is read).
if (check)
{
    // Same as pelly.exe: the Korean rows reach a redirected or Windows console as UTF-8 (MCP mode leaves the console alone).
    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
    using var factory = LoggerFactory.Create(_ => { });
    return await HealthCheck.RunAsync(context, error, MakeBaseline(factory.CreateLogger<BaselineService>()), ["pelly_impact", "pelly_verify"],
        Console.Out, CancellationToken.None);
}

// The HEAD baseline (design §11): prebuilt in the background in the mirror outside the workspace, outside the tool lock.
if (context is not null)
{
    builder.Services.AddSingleton(sp => MakeBaseline(sp.GetRequiredService<ILogger<BaselineService>>())!);
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BaselineService>());
    builder.Services.AddSingleton(sp => new VerifyService(fs, context, new GitRepo(processes, context.Root),
        sp.GetRequiredService<BaselineService>(), verifier!, tests!, VerifyService.DefaultWaitLimit,
        cs => new SqlServerExecutor(cs), processes, OperatingSystem.IsWindows));
}

var version = typeof(ServerContext).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "pellycode-mcp-server", Version = version })
    .WithStdioServerTransport()
    .WithTools<PellyTools>();

await builder.Build().RunAsync();
return 0;
