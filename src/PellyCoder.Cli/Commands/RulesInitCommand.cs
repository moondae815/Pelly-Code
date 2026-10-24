using System.ComponentModel;
using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Rules;
using PellyCoder.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class RulesInitSettings : CommandSettings
{
    [CommandOption("--dry-run")]
    [Description("초안만 보여주고 파일을 쓰지 않음")]
    public bool DryRun { get; init; }

    [CommandOption("--force")]
    [Description("이미 있는 Pelly rules 절을 다시 만듦 (그 절만 바뀜)")]
    public bool Force { get; init; }

    [CommandOption("--yes")]
    [Description("확인 없이 씀 (비대화형 실행용)")]
    public bool Yes { get; init; }

    // A real option so strict parsing accepts it; Program.cs reads the raw arguments (ExitPause), not this value.
    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }
}

/// <summary><c>pelly rules init</c>: drafts the <c>## Pelly rules</c> section from the codebase and writes it into the workspace AGENTS.md after approval.</summary>
public sealed class RulesInitCommand(IFileSystem fs, IAnsiConsole console, IUserInteraction ui, TimeProvider time)
    : AsyncCommand<RulesInitSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RulesInitSettings settings, CancellationToken cancellation)
    {
        var anchor = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "pelly-rules");
        var ws = WorkspaceLocator.Resolve(fs, anchor);
        var path = fs.Path.Combine(ws.Root, RulesLoader.FileName);

        FileSnapshot? existing = null;
        if (fs.File.Exists(path))
        {
            if (fs.FileInfo.New(path).Length > RulesLoader.MaxBytes)
                return Fail($"{RulesLoader.FileName}가 너무 큽니다 ({RulesLoader.MaxBytes / 1024} KiB 초과)");
            try { existing = TextFileCodec.Read(fs, path); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Fail($"{RulesLoader.FileName}을(를) 읽을 수 없습니다: {ex.Message}");
            }
            if (RulesDocument.FindSection(existing.Text) is not null && !settings.Force)
                return Fail("이미 AGENTS.md에 Pelly rules 절이 있습니다. --force로 이 절만 다시 만들거나 `pelly rules check`로 검사하세요");
        }

        var draft = RulesDrafter.Draft(fs, ws.Root, cancellation);
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var section = RulesSectionWriter.BuildSection(draft.Rules, today);

        Print($"분석: C# 파일 {draft.CsFiles}개, SQL 파일 {draft.SqlFiles}개 — 규칙 초안 {draft.Rules.Count}개");
        foreach (var line in section.TrimEnd('\n').Split('\n')) Print(line);
        Print($"대상: {path}");

        if (settings.DryRun) return ExitCodes.Success;

        if (existing is not null && RulesDocument.FindSection(existing.Text) is not null)
        {
            var current = RulesLoader.Load(fs, ws);
            // An empty draft would leave only comments: never let it wipe hand-written rules, even with --yes.
            if (draft.Rules.Count == 0 && current.Rules.Count > 0)
                return Fail($"초안 규칙이 0개라 기존 규칙 {current.Rules.Count.ToString(CultureInfo.InvariantCulture)}개를 지우지 않습니다. 기존 절을 직접 고치세요");
            Print(current.Errors.Count > 0
                ? "기존 절(오류 있음)을 새 초안으로 바꿉니다"
                : $"기존 절의 규칙 {current.Rules.Count.ToString(CultureInfo.InvariantCulture)}개를 새 초안으로 바꿉니다");
        }

        if (!settings.Yes)
        {
            if (!ui.IsInteractive) return Fail("비대화형 실행에서는 --yes가 필요합니다.");
            if (!await ui.ConfirmAsync("AGENTS.md에 쓸까요?", defaultValue: false, cancellation))
            {
                Print("쓰지 않았습니다.");
                return ExitCodes.UserCancelled;
            }
        }

        var newText = RulesSectionWriter.Apply(existing?.Text, section);
        if (existing is null)
        {
            // CreateNew: a file that appeared after the check above is not overwritten.
            try
            {
                using var stream = fs.FileStream.New(path, FileMode.CreateNew, FileAccess.Write);
                stream.Write(new UTF8Encoding(false).GetBytes(newText));
            }
            catch (IOException ex)
            {
                return Fail($"{RulesLoader.FileName}을(를) 쓰지 못했습니다: {ex.Message}");
            }
        }
        else
        {
            var result = await new FileGuard(fs, time).WriteAsync(existing, newText, cancellation);
            if (result.Status != FileWriteStatus.Written)
                return Fail($"{RulesLoader.FileName}을(를) 쓰지 못했습니다: {result.Detail}");
        }

        var check = RulesLoader.Load(fs, ws);
        if (check.Errors.Count > 0)
        {
            console.MarkupLine($"[red]{Markup.Escape($"쓴 뒤 AGENTS.md 규칙 오류 {check.Errors.Count}건")}[/]");
            foreach (var e in check.Errors)
                Print(e.Line == 0 ? $"파일: {e.Message}" : $"줄 {e.Line}: {e.Message}");
            return ExitCodes.EnvError;
        }

        Print($"{path}에 규칙 {draft.Rules.Count.ToString(CultureInfo.InvariantCulture)}개를 썼습니다. 검토한 뒤 `pelly rules check`로 확인하세요.");
        return ExitCodes.Success;
    }

    private void Print(string text) => console.MarkupLine(Markup.Escape(text));

    private int Fail(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return ExitCodes.EnvError;
    }
}
