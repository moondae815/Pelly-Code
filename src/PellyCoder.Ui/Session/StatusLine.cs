using PellyCoder.Core.Fsm;

namespace PellyCoder.Ui.Session;

public enum StatusLineKind { Status, Waiting, Warning, Error, Result }

/// <summary>One progress line. <paramref name="Phase"/> is null for lines not tied to a phase (warnings, errors, result).</summary>
public sealed record StatusLine(RunPhase? Phase, int Attempt, string Text, StatusLineKind Kind);
