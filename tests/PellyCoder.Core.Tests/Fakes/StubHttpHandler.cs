using System.Net;
using System.Text;
using System.Threading.Channels;

namespace PellyCoder.Core.Tests.Fakes;

/// <summary>One request seen by <see cref="StubHttpHandler"/>.</summary>
internal sealed record StubCall(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body, int Index);

/// <summary>Scripted HttpMessageHandler: per-path responses, delays and exceptions; records every request.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<StubCall, CancellationToken, Task<HttpResponseMessage>>> _routes = new();
    private readonly object _gate = new();
    private readonly List<StubCall> _calls = new();

    public IReadOnlyList<StubCall> Calls
    {
        get { lock (_gate) return _calls.ToArray(); }
    }

    public int CountFor(string path) => Calls.Count(c => c.Path == path);

    public StubHttpHandler On(string path, Func<StubCall, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _routes[path] = handler;
        return this;
    }

    public StubHttpHandler OnJson(string path, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(path, (_, _) => Task.FromResult(JsonResponse(json, status)));

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
            headers[h.Key] = string.Join(",", h.Value);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        StubCall call;
        lock (_gate)
        {
            call = new StubCall(request.Method.Method, path, headers, body, _calls.Count(c => c.Path == path));
            _calls.Add(call);
        }

        if (_routes.TryGetValue(path, out var route))
            return await route(call, ct);
        return JsonResponse("""{"error":"unmatched route"}""", HttpStatusCode.NotFound);
    }
}

/// <summary>A response body whose lines the test pushes one at a time (to model a stalled stream).</summary>
internal sealed class ChannelStream : Stream
{
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
    private byte[] _current = [];
    private int _offset;

    public void WriteLine(string line) => _channel.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));
    public void Complete() => _channel.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_offset >= _current.Length)
        {
            if (!await _channel.Reader.WaitToReadAsync(ct))
                return 0;
            if (_channel.Reader.TryRead(out var next))
            {
                _current = next;
                _offset = 0;
            }
        }

        var n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
