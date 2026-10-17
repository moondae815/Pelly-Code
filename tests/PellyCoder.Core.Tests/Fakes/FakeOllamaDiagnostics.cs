using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Tests.Fakes;

/// <summary>Fake <see cref="IOllamaDiagnostics"/> returning a fixed chat prompt token count and recording the inputs.</summary>
public sealed class FakeOllamaDiagnostics : IOllamaDiagnostics
{
    public int? Count { get; set; }
    public Exception? Throws { get; set; }
    public List<(string System, string User)> Calls { get; } = [];

    public Task<int?> ChatPromptTokenCountAsync(string system, string user, CancellationToken ct)
    {
        Calls.Add((system, user));
        if (Throws is not null)
            throw Throws;
        return Task.FromResult(Count);
    }
}
