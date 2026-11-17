using System.IO.Abstractions;
using PellyCode.Core.Abstractions;
using PellyCode.Core.Builds;
using PellyCode.Core.Files;
using PellyCode.Core.Models;
using PellyCode.Core.Sql;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Verify;

/// <summary>
/// The SQL part of <c>pelly_verify</c> (design §7): every object batch of a changed <c>.sql</c> file is compiled under
/// <c>SET NOEXEC ON</c>, and so is the batch of the same object in the file's HEAD text (a renamed file's old path); only errors
/// the HEAD batch did not have are reported, compared like pelly's <c>SqlCompile</c> step (file, code and message; lines ignored).
/// Uses <c>sql.connectionString</c> within <c>sql.allowedHosts</c>·<c>allowedDatabases</c>, never <c>sql.production</c>.
/// </summary>
public sealed class SqlVerifier(IFileSystem fs, ServerContext ctx, Func<string, ISqlExecutor> executors)
{
    public const string SkipHeading = "[건너뜀] SQL 컴파일 검사";
    public const string NoConnection = "SQL 연결이 없습니다";
    public const string NoDatabase = "연결 문자열에 Database(Initial Catalog)가 없습니다";
    public const string NoObjects = "SQL 객체(프로시저·함수·뷰)가 없습니다";

    public static bool IsSql(string path) => path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    /// <param name="head">The HEAD bytes of a repository-relative path; null when HEAD has no such file.</param>
    public async Task CheckAsync(IReadOnlyList<GitChange> changes, Func<string, CancellationToken, Task<byte[]?>> head, VerifyReport report,
        IProgress<string>? progress, CancellationToken ct)
    {
        var files = changes.Where(c => c.Kind != GitChangeKind.Deleted && IsSql(c.Path)).ToList();
        if (files.Count == 0) return;

        var options = ctx.Options.Sql;
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            report.Skip(SkipHeading, NoConnection);
            return;
        }
        var endpoint = SqlConnectionText.Endpoint(options.ConnectionString);
        if (endpoint.Database is null)
        {
            report.Skip(SkipHeading, NoDatabase);
            return;
        }
        var allowed = SqlTargetPolicy.Check(endpoint, options);
        if (!allowed.Allowed)
        {
            report.Skip(SkipHeading, allowed.Message ?? "SQL 접속 대상이 허용되지 않습니다");
            return;
        }

        progress?.Report("바뀐 SQL 파일을 컴파일하는 중입니다");
        var sql = executors(options.ConnectionString);
        foreach (var change in files)
        {
            var section = "[SQL] " + change.Path;
            try
            {
                await CheckFileAsync(sql, change, section, head, report, ct);
            }
            catch (SqlUnavailableException e)
            {
                report.Skip(SkipHeading, $"SQL 서버를 사용할 수 없습니다: {SqlConnectionText.MaskInText(e.Message, options.ConnectionString)}");
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                report.Skip(section, $"파일을 읽지 못했습니다: {e.Message}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                report.Skip(section, $"SQL 컴파일 검사를 할 수 없습니다: {SqlConnectionText.MaskInText(e.Message, options.ConnectionString)}");
            }
        }
    }

    private async Task CheckFileAsync(ISqlExecutor sql, GitChange change, string section,
        Func<string, CancellationToken, Task<byte[]?>> head, VerifyReport report, CancellationToken ct)
    {
        var full = fs.Path.Combine(ctx.Root, change.Path.Replace('/', fs.Path.DirectorySeparatorChar));
        var bytes = fs.File.ReadAllBytes(full);
        var current = TextFileCodec.Decode(full, bytes);
        var headBytes = change.Kind == GitChangeKind.Added ? null : await head(change.OldPath ?? change.Path, ct);
        var headText = headBytes is null ? null : HeadText.Decode(headBytes, current, bytes, b => TextFileCodec.Decode(full, b));

        var compat = ctx.Options.Sql.CompatLevel;
        var now = SqlBatchSplitter.Split(HeadText.Lf(current.Text), compat);
        var objects = now.ObjectBatches.Where(b => b.ObjectName is not null).ToList();
        if (objects.Count == 0)
        {
            report.Skip(section, NoObjects);
            return;
        }
        var before = headText is null ? null : SqlBatchSplitter.Split(headText, compat);
        var unpaired = before?.ObjectBatches.Where(b => b.ObjectName is not null).ToList() ?? [];

        var found = 0;
        foreach (var batch in objects)
        {
            var pair = unpaired.FirstOrDefault(h => h.ObjectName!.SameAs(batch.ObjectName!));
            if (pair is not null) unpaired.Remove(pair);
            var settings = now.SettingsBefore(batch.Index);
            // The same text under the same settings compiles the same: nothing can be new.
            if (pair is not null && pair.Text == batch.Text && before!.SettingsBefore(pair.Index) == settings) continue;

            var errors = await CompileAsync(sql, batch, settings, change.Path, ct);
            if (errors.Count == 0) continue;
            var baseline = pair is null ? [] : await CompileAsync(sql, pair, before!.SettingsBefore(pair.Index), change.Path, ct);
            foreach (var e in BaselineDiff.New(baseline, errors))
            {
                report.Error(section, $"{change.Path}:{e.Line} {e.Code} {e.Message}");
                found++;
            }
        }
        if (found == 0) report.Pass(section);
    }

    /// <summary>The batch's errors with file lines; the file is the current path on both sides so a renamed file's old errors match.</summary>
    private static async Task<IReadOnlyList<Diagnostic>> CompileAsync(ISqlExecutor sql, SqlBatch batch, SqlSessionSettings settings,
        string rel, CancellationToken ct)
    {
        var errors = await sql.CompileWithNoExecAsync(batch.Text, settings, ct);
        return [.. errors.Select(e => new Diagnostic(rel, batch.StartLine + e.Line - 1, 0, "error", $"Msg{e.Number}", e.Message))];
    }
}
