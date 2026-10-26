using Avalonia;
using Avalonia.Markup.Xaml;

namespace PellyCoder.Ui.Views;

/// <summary>The Avalonia application: Fluent light theme only (design §6). The window is created by <see cref="UiHost"/>.</summary>
public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
