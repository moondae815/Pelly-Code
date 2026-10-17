namespace PellyCoder.Core.Abstractions;

/// <summary>Sampling parameters sent with a generation request.</summary>
public sealed record SamplingOptions(
    double Temperature,
    double TopP,
    int TopK,
    double RepeatPenalty,
    double PresencePenalty);
