using ModelContextProtocol;

namespace PellyCode.McpServer.Verify;

/// <summary>
/// Sends an MCP progress notification at every <see cref="IProgress{T}.Report"/> and every <paramref name="interval"/> in
/// between with the last message, so a client that cuts silent calls (OpenCode: 60 s) keeps waiting. Progress values increase.
/// </summary>
public sealed class ProgressHeartbeat : IProgress<string>, IAsyncDisposable
{
    private readonly IProgress<ProgressNotificationValue> _sink;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private string _message = "검사 중입니다";
    private int _count;
    private bool _stopped;

    public ProgressHeartbeat(IProgress<ProgressNotificationValue> sink, TimeProvider time, TimeSpan interval)
    {
        _sink = sink;
        _timer = time.CreateTimer(_ => Send(null), null, interval, interval);
    }

    public void Report(string value) => Send(value);

    private void Send(string? message)
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (message is not null) _message = message;
            _sink.Report(new ProgressNotificationValue { Progress = ++_count, Message = _message });
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _stopped = true;
        await _timer.DisposeAsync();
    }
}
