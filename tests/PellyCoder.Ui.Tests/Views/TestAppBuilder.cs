using Avalonia;
using Avalonia.Headless;
using PellyCoder.Ui.Tests.Views;
using PellyCoder.Ui.Views;

[assembly: AssemblyFixture(typeof(HeadlessSession))]

namespace PellyCoder.Ui.Tests.Views;

/// <summary>
/// Headless Avalonia app for the window tests: the real <see cref="App"/> (Fluent light theme) without a desktop platform.
/// Draws with Skia (not the headless stub), so <see cref="SelfTestTests"/> can check the self-test's pixels.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// One headless UI thread for the test assembly. <c>Avalonia.Headless.XUnit</c> 12.1.4 is built against xunit.v3 3.2.2 and
/// fails test discovery under the repository's xunit.v3 4.0.2 (MissingMethodException), so tests dispatch onto the session here.
/// The session is not disposed: <see cref="HeadlessUnitTestSession"/>.Dispose did not return on macOS (12.1.4), and its
/// dispatcher thread is a background thread that ends with the test process.
/// </summary>
public sealed class HeadlessSession
{
    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    /// <summary>Runs <paramref name="test"/> on the Avalonia UI thread and waits for it.</summary>
    public Task Run(Func<Task> test) =>
        _session.Dispatch(async () =>
        {
            await test();
            return true;
        }, TestContext.Current.CancellationToken);

    public Task Run(Action test) => Run(() =>
    {
        test();
        return Task.CompletedTask;
    });
}
