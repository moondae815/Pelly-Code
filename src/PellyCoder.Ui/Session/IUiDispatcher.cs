namespace PellyCoder.Ui.Session;

/// <summary>
/// Sends work to the UI thread. Every state change of the session and the GUI interaction goes through it.
/// Implementations must run posted actions in the order they were posted (FIFO), e.g. a question is published before it is cleared.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
