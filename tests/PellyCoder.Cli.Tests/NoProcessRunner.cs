using PellyCoder.Core.Abstractions;

namespace PellyCoder.Cli.Tests;

/// <summary>
/// The <see cref="IProcessRunner"/> for command tests that must not start a process: the commands under test reach no build,
/// so any call is a test bug, and a real runner would start <c>dotnet</c> on the test machine instead of failing.
/// </summary>
internal sealed class NoProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct) =>
        throw new InvalidOperationException($"NoProcessRunner: unexpected process {spec.FileName} {string.Join(' ', spec.Arguments)}");
}
