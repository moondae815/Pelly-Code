namespace PellyCoder.Ui.Session;

public enum QuestionKind { Approve, Confirm, Select }

/// <summary>The question the run is waiting on. <paramref name="Choices"/> holds the display labels for <see cref="QuestionKind.Select"/>, otherwise it is empty.</summary>
public sealed record PendingQuestion(QuestionKind Kind, string Message, IReadOnlyList<string> Choices);
