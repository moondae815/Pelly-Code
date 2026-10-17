using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using Spectre.Console;

namespace PellyCoder.Cli;

/// <summary>Composition root: the only place that creates the real file system, <see cref="HttpClient"/> and backend.</summary>
public static class Composition
{
    public static IServiceCollection Services(IUserInteraction ui, IAnsiConsole console, CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(new FileSystem());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(ui);
        services.AddSingleton(console);
        services.AddSingleton(typeof(CancellationToken), ct);
        services.AddSingleton(ConfigSources.FromProcess());
        services.AddSingleton<HttpClientHolder>();
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IRunner, OrchestratorRunner>();
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

    private sealed class OrchestratorRunner(IFileSystem fs, IUserInteraction ui, TimeProvider time, HttpClientHolder http) : IRunner
    {
        public Task<int> RunAsync(RunRequest request, PellyOptions options, CancellationToken ct)
        {
            var backend = new OllamaBackend(http.Get(options.Llm), options.Llm, time);
            return FsmOrchestratorFactory.Create(fs, backend, ui, options, time).RunAsync(request, ct);
        }
    }

    private sealed class DoctorRunner(IFileSystem fs, TimeProvider time, HttpClientHolder http) : IDoctorRunner
    {
        public Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, CancellationToken ct)
        {
            var backend = new OllamaBackend(http.Get(options.Llm), options.Llm, time);
            var template = options.Llm.Template is { } path ? QwenChatTemplate.LoadFromFile(fs, path) : QwenChatTemplate.LoadDefault();
            return new DoctorService(backend, backend, template, QwenTokenCounter.Shared, options, time).RunAsync(ct);
        }
    }
}
