using Avalonia.Threading;
using PellyCoder.Ui.Session;

namespace PellyCoder.Ui.Views;

/// <summary>Posts to the Avalonia UI thread, always at one priority so posted actions run in the order they were posted.</summary>
public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Normal);
}
