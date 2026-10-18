using System.ComponentModel;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

/// <summary>Options shared by <c>pelly cs</c> and <c>pelly sql</c>.</summary>
public abstract class RunSettings : CommandSettings
{
    [CommandOption("--yes")]
    [Description("검토 질문 없이 승인")]
    public bool Yes { get; init; }

    [CommandOption("--dry-run")]
    [Description("파일을 쓰지 않고 diff만 보여줌")]
    public bool DryRun { get; init; }

    [CommandOption("--verbose")]
    [Description("단계별 시간·토큰 요약과 실행 기록 폴더를 보여줌")]
    public bool Verbose { get; init; }

    [CommandOption("--max-retries <N>")]
    [Description("교정 재시도 최대 횟수 (기본: retry.max)")]
    public int? MaxRetries { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("작업 공간 설정 파일 경로 (기본: <루트>/pelly.json)")]
    public string? Config { get; init; }

    [CommandOption("--think <MODE>")]
    [Description("off | retry | always (llm.thinkMode 덮어쓰기)")]
    public string? Think { get; init; }

    [CommandOption("--model <TAG>")]
    [Description("모델 태그 (llm.model 덮어쓰기)")]
    public string? Model { get; init; }

    [CommandOption("--endpoint <URL>")]
    [Description("Ollama 주소 (llm.endpoint 덮어쓰기)")]
    public string? Endpoint { get; init; }

    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }
}
