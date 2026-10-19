using PellyCoder.Core.Doctor;

namespace PellyCoder.Eval;

/// <summary>
/// Hidden <c>pelly-eval --offline-selftest</c>: the same bundled-component rows as <c>pelly doctor --offline-selftest</c>
/// (<see cref="OfflineSelfTest"/>: tokenizer, Roslyn, ScriptDom, CP949, embedded prompts, native SqlClient), so the packaged
/// single file proves its run path works without a server. Touches no config, network or HTTP client. Exit 0 all Ok, 4 otherwise.
/// </summary>
public static class SelfTestCommand
{
    public static int Run(TextWriter output)
    {
        var rows = OfflineSelfTest.Run();
        output.WriteLine("| 항목 | 상태 | 내용 |");
        output.WriteLine("| --- | --- | --- |");
        foreach (var row in rows)
            output.WriteLine($"| {row.Name} | {row.Status} | {row.Detail.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ")} |"
                + (row.Action is { } action ? $" {action}" : ""));
        return rows.All(r => r.Status == DoctorStatus.Ok) ? 0 : 4;
    }
}
