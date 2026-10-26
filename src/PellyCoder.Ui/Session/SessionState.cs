using PellyCoder.Core.Abstractions;

namespace PellyCoder.Ui.Session;

/// <summary>States of the GUI run session (design §5.2). <see cref="Invalid"/>: command validation failed, nothing can run.</summary>
public enum SessionState { Idle, Running, AwaitingAnswer, Cancelling, Finished, Invalid }

/// <summary>One run of the FSM with the GUI interaction; returns the process exit code (0–5).</summary>
public delegate Task<int> RunFunc(string request, IUserInteraction ui, CancellationToken ct);
