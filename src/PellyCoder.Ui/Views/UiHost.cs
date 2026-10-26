using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
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
    /// When a native GUI library beside pelly.exe is missing, writes the reason to <paramref name="error"/> and returns 4
    /// before the session or the window is created.
    /// </summary>
    public static int Run(UiLaunch launch, Func<GuiUserInteraction, UiSession> createSession, TextWriter error) =>
        Run(launch, createSession, error, SetupDesktop);

    /// <summary><see cref="Run(UiLaunch, Func{GuiUserInteraction, UiSession}, TextWriter)"/> with the platform setup replaceable (tests).</summary>
    internal static int Run(UiLaunch launch, Func<GuiUserInteraction, UiSession> createSession, TextWriter error, Action setup) =>
        OnUiThread(() =>
        {
            try
            {
                setup();
            }
            catch (Exception ex) when (MissingNative(ex) is { } library)
            {
                error.WriteLine(MissingNativeMessage(library));
                return ExitCodes.EnvError;
            }

            var app = Application.Current ?? throw new InvalidOperationException("Avalonia application was not created.");
            var ui = new GuiUserInteraction(new AvaloniaDispatcher());
            var session = createSession(ui);
            var window = new MainWindow(new MainWindowModel(launch, session, ui));
            app.Run(window);
            return session.ExitCode;
        });

    /// <summary>
    /// Draws <see cref="MainWindow"/> once with the desktop platform and Skia, without showing it, and writes
    /// "ui selftest: ok (&lt;width&gt;x&lt;height&gt;)" to <paramref name="output"/>. Touches no files.
    /// 0 on success, 4 on failure (the reason is written to <paramref name="error"/>).
    /// </summary>
    public static int SelfTest(TextWriter output, TextWriter error) => SelfTest(output, error, () =>
    {
        Configure().SetupWithoutStarting();
        using var bitmap = RenderWindow();
        return bitmap.PixelSize;
    });

    /// <summary><see cref="SelfTest(TextWriter, TextWriter)"/> with the drawing replaceable (tests); <paramref name="render"/> returns the drawn size.</summary>
    internal static int SelfTest(TextWriter output, TextWriter error, Func<PixelSize> render)
    {
        try
        {
            var size = OnUiThread(render);
            output.WriteLine($"ui selftest: ok ({size.Width}x{size.Height})");
            return ExitCodes.Success;
        }
        catch (Exception ex) when (MissingNative(ex) is { } library)
        {
            error.WriteLine(MissingNativeMessage(library));
            return ExitCodes.EnvError;
        }
        catch (Exception ex)
        {
            error.WriteLine($"GUI 자체 점검 실패: {ex.GetType().Name}: {ex.Message}");
            return ExitCodes.EnvError;
        }
    }

    /// <summary>
    /// Draws a self-test <see cref="MainWindow"/> (fake run, no files) into a bitmap without showing it; the caller disposes it.
    /// Call on the Avalonia UI thread after the platform is set up.
    /// </summary>
    internal static RenderTargetBitmap RenderWindow()
    {
        var ui = new GuiUserInteraction(new AvaloniaDispatcher());
        var session = new UiSession((_, _, _) => Task.FromResult(ExitCodes.Success), ui, [], CancellationToken.None);
        var window = new MainWindow(new MainWindowModel(
            new UiLaunch("Pelly — selftest", "selftest", "selftest", DryRun: false), session, ui));
        // A window that was never shown is invisible, so Measure and Render skip it: draw its template root instead.
        window.ApplyStyling();
        window.ApplyTemplate();
        var root = (Layoutable)window.GetVisualChildren().Single();
        var size = new Size(window.Width, window.Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
        try
        {
            bitmap.Render(root);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        window.Close();
        return bitmap;
    }

    /// <summary>Sets up the desktop platform and loads its native libraries, so a missing DLL fails before any window exists.</summary>
    private static void SetupDesktop()
    {
        Configure().SetupWithoutStarting();
        LoadNatives();
    }

    /// <summary>Loads Skia and HarfBuzz by drawing a line of text off screen. Call on the Avalonia UI thread after the platform is set up.</summary>
    internal static void LoadNatives()
    {
        var probe = new TextBlock { Text = "가A" };
        probe.Measure(Size.Infinity);
        probe.Arrange(new Rect(probe.DesiredSize));
        using var bitmap = new RenderTargetBitmap(new PixelSize(8, 8));
        bitmap.Render(probe);
    }

    /// <summary>The library named by a <see cref="DllNotFoundException"/> in <paramref name="ex"/>'s inner chain (e.g. inside a <see cref="TypeInitializationException"/>), else null.</summary>
    private static string? MissingNative(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is not DllNotFoundException missing) continue;
            // "Unable to load DLL 'libSkiaSharp' or one of its dependencies: ..." (Windows) / "Unable to load shared library 'libSkiaSharp' ..."
            var message = missing.Message;
            var open = message.IndexOf('\'');
            var close = open < 0 ? -1 : message.IndexOf('\'', open + 1);
            return close > open + 1 ? message[(open + 1)..close] : message;
        }
        return null;
    }

    private static string MissingNativeMessage(string library) =>
        $"GUI 구성 요소를 불러오지 못했습니다: {library}. pelly.exe 옆의 DLL을 함께 두십시오.";

    private static AppBuilder Configure() => AppBuilder.Configure<App>().UsePlatformDetect();

    private static T OnUiThread<T>(Func<T> body)
    {
        if (!OperatingSystem.IsWindows()) return body();

        T result = default!;
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
