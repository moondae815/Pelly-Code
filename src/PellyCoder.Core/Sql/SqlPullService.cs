using System.Data.Common;
using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Sql;

public enum PullSource { Prod, Dev }

public sealed record SqlPullRequest(IReadOnlyList<string> Objects, PullSource Source, string OutDirectory, bool Force);

public enum PullItemStatus { Written, Exists, NotFound, UnsupportedKind, Encrypted, NoPermission, BadName, ReadFailed }

/// <param name="Notices">For a failed item the first entry is the reason (Korean); for a written item they are warnings and notes.</param>
public sealed record PullItem(string Input, PullItemStatus Status, string? Path, IReadOnlyList<string> Notices);

/// <summary>
/// Writes SSMS-shaped ALTER scripts with a pull record (design 5). Only read-only reader calls are made; unreachable
/// <paramref name="source"/> surfaces as <see cref="SqlUnavailableException"/>, an unreachable comparison server is a notice.
/// </summary>
public sealed class SqlPullService(IFileSystem fs, TimeProvider time, int compatLevel)
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal) { "P", "FN", "IF", "TF", "V" };

    /// <summary>True for the module kinds <c>pelly sql pull</c> writes: procedures, functions and views.</summary>
    public static bool CanPull(string typeCode) => SupportedTypes.Contains(typeCode);

    /// <param name="compareWith">The dev reader when pulling from production; null when there is nothing to compare with.</param>
    public async Task<IReadOnlyList<PullItem>> PullAsync(SqlPullRequest req, ISqlDefinitionReader source, SqlEndpoint sourceEndpoint,
        ISqlDefinitionReader? compareWith, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var items = new List<PullItem>();
        SqlServerProbe? probe = null;
        var devUnreachable = false;
        var seen = new List<SqlObjectName>();

        foreach (var input in req.Objects)
        {
            ct.ThrowIfCancellationRequested();
            SqlObjectName parsed;
            try { parsed = SqlObjectName.Parse(input); }
            catch (FormatException) { items.Add(Fail(input, PullItemStatus.BadName, null, $"객체 이름을 해석할 수 없습니다: {input}")); continue; }

            var name = new SqlObjectName(parsed.Schema ?? "dbo", parsed.Name);
            if (seen.Any(n => n.SameAs(name))) continue; // the same object named twice is pulled once
            seen.Add(name);
            var fileName = $"{name.Schema}.{name.Name}.sql";
            if (fileName.IndexOfAny(fs.Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains('/') || fileName.Contains('\\'))
            {
                items.Add(Fail(input, PullItemStatus.BadName, null, $"파일 이름으로 쓸 수 없는 객체 이름입니다: {name.Display}"));
                continue;
            }

            var path = fs.Path.Combine(req.OutDirectory, fileName);
            if (!req.Force && fs.File.Exists(path))
            {
                items.Add(Fail(input, PullItemStatus.Exists, path, $"이미 있는 파일입니다 (덮어쓰려면 --force): {path}"));
                continue;
            }

            SqlModuleDefinition? module;
            try
            {
                module = await source.GetDefinitionAsync(name, ct);
                if (module is { Definition: null } && SupportedTypes.Contains(module.TypeCode)) probe ??= await source.ProbeAsync(ct);
            }
            catch (DbException e) when (!ct.IsCancellationRequested)
            {
                // A query-level SqlException (permission, lock timeout…) fails this object only; the others are still pulled.
                items.Add(Fail(input, PullItemStatus.ReadFailed, null, $"{name.Display}의 정의를 읽지 못했습니다: {SqlConnectionText.MaskInText(e.Message, null)}"));
                continue;
            }
            if (module is null)
            {
                items.Add(Fail(input, PullItemStatus.NotFound, null, $"{name.Display}이(가) {(req.Source == PullSource.Prod ? "운영" : "개발")} DB에 없습니다"));
                continue;
            }
            if (!SupportedTypes.Contains(module.TypeCode))
            {
                items.Add(Fail(input, PullItemStatus.UnsupportedKind, null,
                    $"{name.Display}은(는) 받을 수 없는 종류입니다 (프로시저·함수·뷰만 받습니다, 종류 {module.TypeCode})"));
                continue;
            }
            if (module.Definition is null)
            {
                items.Add(probe!.CanViewDefinition
                    ? Fail(input, PullItemStatus.Encrypted, null, $"암호화된 객체는 받을 수 없습니다: {name.Display}")
                    : Fail(input, PullItemStatus.NoPermission, null, $"정의를 볼 권한이 없습니다 (VIEW DEFINITION): {name.Display}"));
                continue;
            }

            var notices = new List<string>();
            var hash = SqlDefinitionText.Hash(module.Definition, compatLevel);
            string? devDiffers = null;
            if (compareWith is not null && req.Source == PullSource.Prod)
            {
                if (devUnreachable)
                    notices.Add("개발 DB와 비교하지 못함");
                else
                {
                    try
                    {
                        var dev = await compareWith.GetDefinitionAsync(name, ct);
                        if (dev is null)
                            notices.Add($"{name.Display}: 개발 DB에 없음 — pelly sql 검증이 실패할 수 있습니다");
                        else if (dev.Definition is null)
                            notices.Add("개발 DB와 비교하지 못함 (개발 DB의 정의를 읽을 수 없음)");
                        else
                        {
                            var devHash = SqlDefinitionText.Hash(dev.Definition, compatLevel);
                            if (devHash != hash)
                            {
                                devDiffers = devHash;
                                notices.Add($"{name.Display}: 개발 DB의 정의가 운영과 다릅니다");
                            }
                        }
                    }
                    catch (SqlUnavailableException)
                    {
                        devUnreachable = true;
                        notices.Add("개발 DB와 비교하지 못함");
                    }
                    catch (DbException) when (!ct.IsCancellationRequested)
                    {
                        notices.Add("개발 DB와 비교하지 못함");
                    }
                }
            }

            var record = new PullRecord(req.Source == PullSource.Prod ? "prod" : "dev", sourceEndpoint.DataSource, sourceEndpoint.Database ?? "",
                new SqlObjectName(module.Name.Schema ?? "dbo", module.Name.Name), hash, time.GetUtcNow());
            var text = SqlDefinitionText.RenderPullFile(module, record, devDiffers, compatLevel);
            fs.Directory.CreateDirectory(req.OutDirectory);
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            fs.File.WriteAllBytes(path, [.. utf8.GetPreamble(), .. utf8.GetBytes(text)]);
            items.Add(new PullItem(input, PullItemStatus.Written, path, notices));
        }
        return items;
    }

    private static PullItem Fail(string input, PullItemStatus status, string? path, string reason) => new(input, status, path, [reason]);
}
