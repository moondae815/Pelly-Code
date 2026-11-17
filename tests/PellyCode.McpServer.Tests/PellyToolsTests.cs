using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PellyCode.Core.Config;
using PellyCode.Core.Workspaces;
using PellyCode.McpServer.Tools;

namespace PellyCode.McpServer.Tests;

/// <summary>Unexpected exceptions inside a tool come back as the tool's Korean text (and go to the log), not as the SDK's generic error.</summary>
public sealed class PellyToolsTests : IDisposable
{
    private readonly string _root = TempRepo.Create();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TempRepo.Delete(_root);

    private ServerContext Context() => new(_root, new PellyOptions(), new Workspace(_root, Path.Combine(_root, ".pelly")));

    [Fact]
    public async Task Impact_UnexpectedException_ReturnsKoreanTextAndLogs()
    {
        var log = new ListLogger();
        var tools = new PellyTools(new ServerState(Context(), null), new ExistsThrowsFileSystem(), new FailingLoader(),
            new ServiceCollection().BuildServiceProvider(), log);

        var text = await tools.Impact("A.cs", 1, null, Ct);

        Assert.Equal("검사 중 오류가 났습니다: boom", text);
        Assert.Contains(log.Messages, m => m.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_UnexpectedException_ReturnsKoreanText()
    {
        var log = new ListLogger();
        var tools = new PellyTools(new ServerState(Context(), null), new FileSystem(), new FailingLoader(),
            new ServiceCollection().BuildServiceProvider(), log);

        var text = await tools.Guarded(() => throw new InvalidOperationException("kaboom"));

        Assert.Equal("검사 중 오류가 났습니다: kaboom", text);
        Assert.Contains(log.Messages, m => m.Contains("kaboom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_Cancellation_StillThrows()
    {
        var tools = new PellyTools(new ServerState(Context(), null), new FileSystem(), new FailingLoader(),
            new ServiceCollection().BuildServiceProvider(), new ListLogger());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tools.Guarded(() => throw new OperationCanceledException()));
    }

    private sealed class ExistsThrowsFileSystem : FileSystem, IFileSystem
    {
        private readonly IFile _file;
        public ExistsThrowsFileSystem() => _file = new ThrowingFile(this);
        IFile IFileSystem.File => _file;

        private sealed class ThrowingFile(IFileSystem fs) : FileWrapper(fs)
        {
            public override bool Exists(string? path) => throw new NotSupportedException("boom");
        }
    }

    private sealed class ListLogger : ILogger<PellyTools>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + " " + exception?.Message);
    }
}
