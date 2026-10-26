using PellyCoder.Ui.Session;

namespace PellyCoder.Ui.Tests.Session;

/// <summary>Runs every posted action immediately on the posting thread.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
