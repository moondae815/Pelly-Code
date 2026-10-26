using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using PellyCoder.Core.Fsm;
using PellyCoder.Ui.Session;

namespace PellyCoder.Ui.Views;

/// <summary>
/// What the window shows before any run. <paramref name="Title"/>: "Pelly — &lt;file name&gt;".
/// <paramref name="TargetLabel"/>: e.g. "OrderService.cs 120번 줄", "OrderService.cs · Cancel", "orders.sql · dbo.MyProc".
/// </summary>
public sealed record UiLaunch(string Title, string TargetLabel, string? InitialRequest, bool DryRun);

/// <summary>Starts Avalonia on the desktop platform. Each method sets Avalonia up, so call one of them once per process.</summary>
public static class UiHost
{
    /// <summary>
    /// Shows the window with the desktop platform, waits until it is closed and returns <see cref="UiSession.ExitCode"/>.
    /// Windows: Avalonia runs on a new STA thread that is joined. Elsewhere it runs on the calling thread (development only).
    /// </summary>
    public static int Run(UiLaunch launch, Func<GuiUserInteraction, UiSession> createSession) => OnUiThread(() =>
    {
        var app = Configure().SetupWithoutStarting().Instance
                  ?? throw new InvalidOperationException("Avalonia application was not created.");
        var ui = new GuiUserInteraction(new AvaloniaDispatcher());
        var session = createSession(ui);
        var window = new MainWindow(new MainWindowModel(launch, session, ui));
        app.Run(window);
        return session.ExitCode;
    });

    /// <summary>
    /// Draws <see cref="MainWindow"/> once into a <see cref="RenderTargetBitmap"/> with the desktop platform and Skia,
    /// without showing it. 0 on success, 4 on failure (the reason is written to <paramref name="error"/>).
    /// </summary>
    public static int SelfTest(TextWriter error)
    {
        try
        {
            return OnUiThread(() =>
            {
                Configure().SetupWithoutStarting();
                var ui = new GuiUserInteraction(new AvaloniaDispatcher());
                var session = new UiSession((_, _, _) => Task.FromResult(ExitCodes.Success), ui, [], CancellationToken.None);
                var window = new MainWindow(new MainWindowModel(
                    new UiLaunch("Pelly — selftest", "selftest", "selftest", DryRun: false), session, ui));
                var size = new Size(window.Width, window.Height);
                window.Measure(size);
                window.Arrange(new Rect(size));
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
                bitmap.Render(window);
                window.Close();
                return ExitCodes.Success;
            });
        }
        catch (Exception ex)
        {
            error.WriteLine($"GUI 자체 점검 실패: {ex.GetType().Name}: {ex.Message}");
            return ExitCodes.EnvError;
        }
    }

    private static AppBuilder Configure() => AppBuilder.Configure<App>().UsePlatformDetect();

    private static int OnUiThread(Func<int> body)
    {
        if (!OperatingSystem.IsWindows()) return body();

        var result = 0;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
