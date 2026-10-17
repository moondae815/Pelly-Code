namespace PellyCoder.Core.Files;

public static class SharingViolationRetry
{
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    /// <summary>
    /// 공유 위반(32)·잠금 위반(33)일 때만 <paramref name="delay"/> 간격으로 재시도한다.
    /// 성공하면 true, 모든 시도가 공유/잠금 위반이면 false. 그 외 예외는 그대로 던진다.
    /// </summary>
    public static async Task<bool> RunAsync(Action action, int maxAttempts, TimeSpan delay, TimeProvider time, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                action();
                return true;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is SharingViolation or LockViolation)
            {
                if (attempt >= maxAttempts) return false;
            }
            await Task.Delay(delay, time, ct);
        }
    }
}
