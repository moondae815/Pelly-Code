using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Tests.Files;

public sealed class SharingViolationRetryTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(200);
    private static IOException Violation(int hresult = unchecked((int)0x80070020)) => new("busy", hresult);

    private static async Task<bool> Drive(Task<bool> task, FakeTimeProvider time)
    {
        while (!task.IsCompleted)
        {
            time.Advance(Delay);
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        return await task;
    }

    [Fact]
    public async Task RetriesSharingViolation_ThenSucceeds()
    {
        var time = new FakeTimeProvider();
        int calls = 0;
        var task = SharingViolationRetry.RunAsync(() => { if (++calls < 3) throw Violation(); }, 5, Delay, time, TestContext.Current.CancellationToken);

        Assert.True(await Drive(task, time));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RetriesLockViolation()
    {
        var time = new FakeTimeProvider();
        int calls = 0;
        var task = SharingViolationRetry.RunAsync(() => { if (++calls < 2) throw Violation(unchecked((int)0x80070021)); }, 5, Delay, time, TestContext.Current.CancellationToken);

        Assert.True(await Drive(task, time));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GivesUpAfterMaxAttempts()
    {
        var time = new FakeTimeProvider();
        int calls = 0;
        var task = SharingViolationRetry.RunAsync(() => { calls++; throw Violation(); }, 5, Delay, time, TestContext.Current.CancellationToken);

        Assert.False(await Drive(task, time));
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task OtherIOException_IsRethrown()
    {
        var time = new FakeTimeProvider();
        int calls = 0;
        await Assert.ThrowsAsync<IOException>(() =>
            SharingViolationRetry.RunAsync(() => { calls++; throw new IOException("disk full", unchecked((int)0x80070070)); }, 5, Delay, time, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }
}
