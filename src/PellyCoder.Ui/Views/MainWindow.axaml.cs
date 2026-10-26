using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PellyCoder.Ui.Views;

/// <summary>
/// The approval window (design §3, §6). Closing goes through <see cref="Session.UiSession.RequestClose"/>: while a run is
/// active the close is cancelled and the window closes on <see cref="Session.UiSession.CloseReady"/> after the rollback.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly MainWindowModel? _model;

    /// <summary>For the XAML loader and designer only.</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowModel model) : this()
    {
        _model = model;
        DataContext = model;
        model.Session.CloseReady += Close;
        model.Lines.CollectionChanged += OnLinesChanged;
        // Tunnel so Ctrl+Enter and Esc are seen before the multi-line request box handles them.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => RequestBox.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_model is not null && !_model.Session.RequestClose()) e.Cancel = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_model is not null)
        {
            _model.Session.CloseReady -= Close;
            _model.Lines.CollectionChanged -= OnLinesChanged;
        }
        base.OnClosed(e);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is null) return;
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _model.Run();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (!_model.Escape()) Close();
            e.Handled = true;
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => LinesScroll.ScrollToEnd(), DispatcherPriority.Background);

    private void OnRunClick(object? sender, RoutedEventArgs e) => _model?.Run();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => _model?.Cancel();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnYesClick(object? sender, RoutedEventArgs e) => _model?.Answer(true);

    private void OnNoClick(object? sender, RoutedEventArgs e) => _model?.Answer(false);

    private void OnSelectClick(object? sender, RoutedEventArgs e) => _model?.Select();
}
