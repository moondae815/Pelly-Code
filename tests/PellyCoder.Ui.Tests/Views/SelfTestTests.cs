using System.Runtime.InteropServices;
using Avalonia;
using PellyCoder.Ui.Views;

namespace PellyCoder.Ui.Tests.Views;

public sealed class SelfTestTests(HeadlessSession headless)
{
    private const string MissingSkia = "GUI 구성 요소를 불러오지 못했습니다: libSkiaSharp. pelly.exe 옆의 DLL을 함께 두십시오.";

    private static readonly UiLaunch Launch = new("Pelly — A.cs", "A.cs 1번 줄", null, DryRun: false);

    [Fact]
    public Task SelfTest_RendersNonEmptyBitmap() => headless.Run(() =>
    {
        using var bitmap = UiHost.RenderWindow();

        var size = bitmap.PixelSize;
        Assert.True(size.Width > 0 && size.Height > 0);
        var stride = size.Width * 4;
        var pixels = new int[size.Width * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length * 4, stride);
        }
        finally
        {
            handle.Free();
        }
        Assert.True(pixels.Distinct().Skip(1).Any(), "every pixel has the same color");
    });

    [Fact]
    public Task LoadNatives_DrawsTextWithoutAWindow() => headless.Run(UiHost.LoadNatives);

    [Fact]
    public void SelfTest_Ok_PrintsSize()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = UiHost.SelfTest(output, error, () => new PixelSize(640, 480));

        Assert.Equal(0, exit);
        Assert.Equal("ui selftest: ok (640x480)" + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void SelfTest_MissingNative_Exit4WithMessage()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = UiHost.SelfTest(output, error, () => throw new DllNotFoundException("libSkiaSharp"));

        Assert.Equal(4, exit);
        Assert.Equal(MissingSkia + Environment.NewLine, error.ToString());
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void SelfTest_MissingNativeInTypeInitializer_NamesTheLibrary()
    {
        var error = new StringWriter();
        var missing = new DllNotFoundException(
            "Unable to load DLL 'libSkiaSharp' or one of its dependencies: The specified module could not be found. (0x8007007E)");

        var exit = UiHost.SelfTest(TextWriter.Null, error,
            () => throw new TypeInitializationException("SkiaSharp.SKObject", missing));

        Assert.Equal(4, exit);
        Assert.Equal(MissingSkia + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void SelfTest_OtherFailure_Exit4WithReason()
    {
        var error = new StringWriter();

        var exit = UiHost.SelfTest(TextWriter.Null, error, () => throw new InvalidOperationException("no display"));

        Assert.Equal(4, exit);
        Assert.Contains("no display", error.ToString());
        Assert.DoesNotContain("DLL을 함께", error.ToString());
    }

    [Fact]
    public void Run_MissingNative_Exit4BeforeTheSessionAndWindow()
    {
        var error = new StringWriter();
        var sessions = 0;

        var exit = UiHost.Run(Launch, _ =>
        {
            sessions++;
            throw new InvalidOperationException("no session expected");
        }, error, setup: () => throw new TypeInitializationException(
            "HarfBuzzSharp.Buffer", new DllNotFoundException("Unable to load DLL 'libHarfBuzzSharp' or one of its dependencies")));

        Assert.Equal(4, exit);
        Assert.Equal(0, sessions);
        Assert.Equal("GUI 구성 요소를 불러오지 못했습니다: libHarfBuzzSharp. pelly.exe 옆의 DLL을 함께 두십시오." + Environment.NewLine,
            error.ToString());
    }

    [Fact]
    public void Run_OtherSetupFailure_Exit4WithReasonOnStderr()
    {
        var error = new StringWriter();
        var sessions = 0;

        var exit = UiHost.Run(Launch, _ =>
        {
            sessions++;
            throw new InvalidOperationException("no session expected");
        }, error, setup: () => throw new InvalidOperationException("no display"));

        Assert.Equal(4, exit);
        Assert.Equal(0, sessions);
        Assert.Equal("GUI 창을 만들지 못했습니다: InvalidOperationException: no display" + Environment.NewLine, error.ToString());
    }
}
