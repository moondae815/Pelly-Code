using PellyCoder.Ui.Views;

namespace PellyCoder.Ui.Tests.Views;

/// <summary>
/// The file check before Avalonia starts (design §7.2). On Windows a missing Skia native does not surface as a
/// <see cref="DllNotFoundException"/> but as a type-initializer fault inside SkiaSharp (CI run 40331719146), so the
/// files beside pelly.exe are checked by name first.
/// </summary>
public sealed class NativeFilesTests
{
    private const string Dir = "/app";

    private static Func<string, bool> Present(params string[] names) =>
        path => names.Any(n => path == Path.Combine(Dir, n));

    [Fact]
    public void AllPresent_NoMissingFile() =>
        Assert.Null(UiHost.MissingNativeFile(Dir, Present("libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll")));

    [Fact]
    public void SkiaMissing_NamesIt() =>
        Assert.Equal("libSkiaSharp.dll", UiHost.MissingNativeFile(Dir, Present("libHarfBuzzSharp.dll", "av_libglesv2.dll")));

    [Fact]
    public void HarfBuzzMissing_NamesIt() =>
        Assert.Equal("libHarfBuzzSharp.dll", UiHost.MissingNativeFile(Dir, Present("libSkiaSharp.dll", "av_libglesv2.dll")));

    [Fact]
    public void OnlyAngleMissing_IsNotRequired() =>
        Assert.Null(UiHost.MissingNativeFile(Dir, Present("libSkiaSharp.dll", "libHarfBuzzSharp.dll")));

    [Fact]
    public void CheckNatives_OnWindowsWithFileMissing_Exit4WithHint()
    {
        var error = new StringWriter();

        var exit = UiHost.CheckNatives(error, isWindows: true, Dir, Present("libHarfBuzzSharp.dll"));

        Assert.Equal(4, exit);
        Assert.Equal("GUI 구성 요소를 불러오지 못했습니다: libSkiaSharp.dll. pelly.exe 옆의 DLL을 함께 두십시오." + Environment.NewLine,
            error.ToString());
    }

    [Fact]
    public void CheckNatives_NotWindows_Skipped()
    {
        var error = new StringWriter();

        Assert.Null(UiHost.CheckNatives(error, isWindows: false, Dir, _ => false));
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void CheckNatives_OnWindowsAllPresent_Continues() =>
        Assert.Null(UiHost.CheckNatives(TextWriter.Null, isWindows: true, Dir, _ => true));
}
