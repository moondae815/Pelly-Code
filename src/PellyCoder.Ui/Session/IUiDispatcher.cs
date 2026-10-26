namespace PellyCoder.Ui.Session;

/// <summary>Sends work to the UI thread. Every state change of the session and the GUI interaction goes through it.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
