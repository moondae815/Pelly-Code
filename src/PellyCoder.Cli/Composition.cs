using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Sql;
using PellyCoder.Ui.Session;
using PellyCoder.Ui.Views;
using Spectre.Console;

namespace PellyCoder.Cli;

/// <summary>A runner that asks its questions through <paramref name="ui"/> (<c>pelly ui</c> passes the window's interaction).</summary>
public delegate IRunner RunnerFactory(IUserInteraction ui);

/// <summary>Shows the approval window until it is closed and returns the session's exit code; tests replace it.</summary>
public interface IUiLauncher
{
    int Run(UiLaunch launch, Func<GuiUserInteraction, UiSession> createSession);
}

/// <summary><c>pelly ui --selftest</c>: draws the window once off screen and returns 0, or 4 with the reason on stderr; tests replace it.</summary>
public interface IUiSelfTest
{
    int Run();
}

/// <summary>Composition root: the only place that creates the real file system, process runner, <see cref="HttpClient"/> and backend.</summary>
public static class Composition
{
    public static IServiceCollection Services(IUserInteraction ui, IAnsiConsole console, CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(new FileSystem());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProcessRunner>(new SystemProcessRunner(TimeProvider.System));
        services.AddSingleton(ui);
        services.AddSingleton(console);
        services.AddSingleton(typeof(CancellationToken), ct);
        services.AddSingleton(ConfigSources.FromProcess());
        services.AddSingleton<HttpClientHolder>();
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IRunner, OrchestratorRunner>();
        services.AddSingleton<RunnerFactory>(sp => runUi => new OrchestratorRunner(
            sp.GetRequiredService<IFileSystem>(), runUi, sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<HttpClientHolder>(), sp.GetRequiredService<IProcessRunner>()));
        services.AddSingleton<IUiLauncher, UiHostLauncher>();
        services.AddSingleton<IUiSelfTest, UiHostSelfTest>();
        services.AddSingleton<IDoctorRunner, DoctorRunner>();
        return services;
    }

    /// <summary>One <see cref="HttpClient"/> per process, created on first use with the then-current connect timeout.</summary>
    public sealed class HttpClientHolder : IDisposable
    {
        private HttpClient? _client;

        public HttpClient Get(LlmOptions llm) => _client ??= new HttpClient(
            new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(llm.ConnectTimeoutSeconds) })
        {
            // OllamaBackend enforces its own timers; the default 100 s would cut a long generation.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        public void Dispose() => _client?.Dispose();
    }

    private sealed class OrchestratorRunner(
        IFileSystem fs, IUserInteraction ui, TimeProvider time, HttpClientHolder http, IProcessRunner processes) : IRunner
    {
        public Task<RunResult> RunDetailedAsync(RunRequest request, PellyOptions options, CancellationToken ct)
        {
            var backend = new OllamaBackend(http.Get(options.Llm), options.Llm, time);
            if (request.Kind == EditKind.Sql)
                return FsmOrchestratorFactory.CreateSql(fs, backend, ui, options, time, new SqlServerExecutor(options.Sql.ConnectionString))
                    .RunDetailedAsync(request, ct);
            return FsmOrchestratorFactory.Create(fs, backend, ui, options, time, processes).RunDetailedAsync(request, ct);
        }
    }

    private sealed class UiHostLauncher : IUiLauncher
    {
        public int Run(UiLaunch launch, Func<GuiUserInteraction, UiSession> createSession) =>
            UiHost.Run(launch, createSession, Console.Error);
    }

    private sealed class UiHostSelfTest : IUiSelfTest
    {
        public int Run() => UiHost.SelfTest(Console.Out, Console.Error);
    }

    private sealed class DoctorRunner(IFileSystem fs, TimeProvider time, HttpClientHolder http, IProcessRunner processes) : IDoctorRunner
    {
        public Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, DoctorContext context, CancellationToken ct)
        {
            var backend = new OllamaBackend(http.Get(options.Llm), options.Llm, time);
            var template = options.Llm.Template is { } path ? QwenChatTemplate.LoadFromFile(fs, path) : QwenChatTemplate.LoadDefault();
            ISqlExecutor? sql = string.IsNullOrWhiteSpace(options.Sql.ConnectionString)
                ? null
                : new SqlServerExecutor(options.Sql.ConnectionString);
            return new DoctorService(backend, backend, template, QwenTokenCounter.Shared, options, time, fs, processes, sql: sql, context: context with { BaseDirectory = AppContext.BaseDirectory },
                semantic: new MsBuildSemanticLoader(fs)).RunAsync(ct);
        }
    }
}
