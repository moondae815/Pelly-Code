using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Config;

/// <summary>How the THINK phase is used (design D21).</summary>
public enum ThinkMode { Off, Retry, Always }

public sealed class PellyOptions
{
    public LlmOptions Llm { get; set; } = new();
    public BudgetOptions Budget { get; set; } = new();
    public SamplingSet Sampling { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
    public LogOptions Log { get; set; } = new();
}

public sealed class LlmOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3.6:35b-a3b";
    public string KeepAlive { get; set; } = "30m";
    public int NumCtx { get; set; } = 8192;
    public Dictionary<string, string> Headers { get; set; } = new();
    public int ConnectTimeoutSeconds { get; set; } = 3;
    public int StreamIdleTimeoutSeconds { get; set; } = 10;
    public string? Template { get; set; }
    public ThinkMode ThinkMode { get; set; } = ThinkMode.Retry;
}

public sealed class BudgetOptions
{
    public int InputTotal { get; set; } = 4096;
    public int System { get; set; } = 600;
    public int Request { get; set; } = 200;
    public int Context { get; set; } = 3000;
    public int Feedback { get; set; } = 296;
    public int Think { get; set; } = 512;
    public int CodeOutput { get; set; } = 2048;
}

public sealed class SamplingSet
{
    public SamplingOptions Think { get; set; } = new(0.6, 0.95, 20, 1.0, 0.0);
    public SamplingOptions Code { get; set; } = new(0.1, 0.9, 20, 1.05, 0.0);
}

public sealed class RetryOptions
{
    public int Max { get; set; } = 3;
}

public sealed class LogOptions
{
    public int RetentionDays { get; set; } = 30;
}
