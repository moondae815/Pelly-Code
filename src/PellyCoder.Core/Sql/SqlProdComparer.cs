using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Sql;

public enum ProdCheckOutcome { NotConfigured, SameAsProd, UnchangedSincePull, ChangedSincePull, DiffersNoRecord, NotInProd, Unavailable }

public sealed record ProdCheck(ProdCheckOutcome Outcome, string? FileHash, string? ProdHash, string? PulledHash, string Message);

/// <summary>Production comparison verdict for a target batch (design 6.2-6.4). Reads production only.</summary>
public static class SqlProdComparer
{
    public static async Task<ProdCheck> CompareAsync(string fileTextLf, SqlBatch target, SqlObjectName targetName,
        SqlReaderOpen prod, int compatLevel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileTextLf);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetName);
        ArgumentNullException.ThrowIfNull(prod);

        if (prod.Reader is null && prod.Problem is null)
            return new(ProdCheckOutcome.NotConfigured, null, null, null, "운영 연결이 설정되지 않아 운영 비교를 건너뜁니다");

        var fileHash = SqlDefinitionText.Hash(target.Text, compatLevel);
        if (prod.Reader is null || prod.Endpoint is null)
            return Unavailable(fileHash, prod.Problem ?? "운영 연결을 만들 수 없습니다");

        var display = $"{targetName.Schema ?? "dbo"}.{targetName.Name}";
        SqlModuleDefinition? module;
        try
        {
            module = await prod.Reader.GetDefinitionAsync(targetName, ct).ConfigureAwait(false);
        }
        catch (SqlUnavailableException e)
        {
            return Unavailable(fileHash, e.Message);
        }

        if (module is null)
            return new(ProdCheckOutcome.NotInProd, fileHash, null, null, $"운영에 {display}가 없습니다 (새 객체)");
        if (module.Definition is null)
            return Unavailable(fileHash, $"{display}의 정의를 읽을 수 없습니다 (암호화되었거나 권한이 없습니다)");

        var prodHash = SqlDefinitionText.Hash(module.Definition, compatLevel);
        if (fileHash == prodHash)
            return new(ProdCheckOutcome.SameAsProd, fileHash, prodHash, null, $"{display}가 운영 정의와 같습니다");

        var record = PullRecord.TryParse(fileTextLf);
        if (record is not null && IsValid(record, prod.Endpoint, targetName))
        {
            if (record.Sha256 == prodHash)
                return new(ProdCheckOutcome.UnchangedSincePull, fileHash, prodHash, record.Sha256, $"받은 뒤 운영의 {display}는 바뀌지 않았습니다");
            var at = record.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            return new(ProdCheckOutcome.ChangedSincePull, fileHash, prodHash, record.Sha256,
                $"받은 뒤 운영에서 {display}가 바뀌었습니다 (받은 시각 {at}). 운영 변경을 덮어쓰지 않도록 다시 받으십시오: pelly sql pull {display} --force. 이미 고친 내용이 있으면 다시 받기 전에 파일을 따로 보관하십시오.");
        }

        return new(ProdCheckOutcome.DiffersNoRecord, fileHash, prodHash, null, $"파일의 {display}가 운영 정의와 다릅니다");
    }

    private static bool IsValid(PullRecord r, SqlEndpoint prod, SqlObjectName target) =>
        r.Source == "prod"
        && SqlTargetPolicy.SameTarget(new SqlEndpoint(r.Server, r.Server, r.Database), prod)
        && r.Object.SameAs(target);

    private static ProdCheck Unavailable(string fileHash, string reason) =>
        new(ProdCheckOutcome.Unavailable, fileHash, null, null, $"운영 정의와 비교하지 못했습니다: {reason}");
}
