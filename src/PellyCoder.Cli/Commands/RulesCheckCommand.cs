using System.ComponentModel;
using System.Globalization;
using System.IO.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Rules;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class RulesCheckSettings : CommandSettings
{
    // A real option so strict parsing accepts it; Program.cs reads the raw arguments (ExitPause), not this value.
    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("작업 공간 설정 파일 경로 (기본: <루트>/pelly.json)")]
    public string? Config { get; init; }
}

/// <summary><c>pelly rules check</c>: validates the <c>## Pelly rules</c> section of the workspace AGENTS.md; exit 4 on errors.</summary>
public sealed class RulesCheckCommand(IFileSystem fs, OptionsResolver resolver, IAnsiConsole console) : Command<RulesCheckSettings>
{
    public override int Execute(CommandContext context, RulesCheckSettings settings, CancellationToken cancellation)
    {
        // The workspace is the current directory; the placeholder file name is never read.
        var anchor = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "pelly-rules");
        var loaded = resolver.Resolve(anchor, settings.Config, null, null, null);
        if (loaded.Options is null)
        {
            foreach (var error in loaded.Errors) console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return ExitCodes.EnvError;
        }

        var ws = WorkspaceLocator.Resolve(fs, anchor);
        var result = RulesLoader.Load(fs, ws);
        if (result.Errors.Count > 0)
        {
            console.MarkupLine($"[red]{Markup.Escape($"AGENTS.md 규칙 오류 {result.Errors.Count}건")}[/]");
            foreach (var e in result.Errors)
                console.MarkupLine(Markup.Escape(e.Line == 0 ? $"파일: {e.Message}" : $"줄 {e.Line}: {e.Message}"));
            return ExitCodes.EnvError;
        }
        if (result.Rules.Count == 0)
        {
            console.MarkupLine(Markup.Escape("규칙이 없습니다 (AGENTS.md의 ## Pelly rules 절). `pelly rules init`으로 초안을 만들 수 있습니다."));
            return ExitCodes.Success;
        }

        var counter = QwenTokenCounter.Shared;
        var table = new Table().AddColumns("줄", "태그", "규칙", "토큰", "forbid");
        var alwaysCount = 0;
        var alwaysTokens = 0;
        foreach (var rule in result.Rules)
        {
            var tokens = counter.Count(RuleSelector.Render([rule]) + "\n");
            if (rule.IsAlways)
            {
                alwaysCount++;
                alwaysTokens += tokens;
            }
            table.AddRow(
                new Markup(rule.Line.ToString(CultureInfo.InvariantCulture)),
                new Markup(Markup.Escape(string.Join(",", rule.Tags))),
                new Markup(Markup.Escape(rule.Text)),
                new Markup(tokens.ToString(CultureInfo.InvariantCulture)),
                new Markup(Markup.Escape(rule.Forbid ?? "-")));
        }
        console.Write(table);

        var budget = loaded.Options.Budget.Rules;
        console.MarkupLine(Markup.Escape(
            $"규칙 {result.Rules.Count}개, 항상 넣는 규칙 {alwaysCount}개 {alwaysTokens} 토큰 / budget.rules {budget}"));
        if (alwaysTokens > budget)
            console.MarkupLine($"[yellow]{Markup.Escape("항상 넣는 규칙만으로 예산을 넘어 아래쪽 규칙이 빠집니다")}[/]");
        return ExitCodes.Success;
    }
}
