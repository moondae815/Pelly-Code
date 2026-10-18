using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Doctor;

/// <summary>
/// Hidden <c>pelly doctor --offline-selftest</c>: exercises the bundled components without network, config files or
/// <c>HttpClient</c>, so a packaged single-file build can prove its embedded dependencies work. The SqlClient row only
/// opens a local named pipe that does not exist (Windows) or constructs a connection (elsewhere).
/// </summary>
public static class OfflineSelfTest
{
    public static IReadOnlyList<DoctorCheck> Run() =>
    [
        Row("토크나이저", Tokenizer),
        Row("Roslyn", RoslynParse),
        Row("ScriptDom", ScriptDom),
        Row("인코딩", Cp949RoundTrip),
        Row("리소스", Resources),
        Row("SqlClient", SqlServerExecutor.SelfTestNativeLayer),
    ];

    private static DoctorCheck Row(string name, Func<string> check)
    {
        try
        {
            return new DoctorCheck(name, DoctorStatus.Ok, check(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DoctorCheck(name, DoctorStatus.Fail, $"{ex.GetType().Name}: {ex.Message}", "배포본이 손상되었을 수 있습니다. 다시 받아 설치하세요.");
        }
    }

    private static string Tokenizer()
    {
        var count = QwenTokenCounter.Shared.Count("Hello, 세계! class A { }");
        return count > 0 ? $"토큰 {count}개" : throw new InvalidOperationException("토큰 수가 0입니다.");
    }

    private static string RoslynParse()
    {
        const string legacy = "using System; class A { int M(int x) { return x > 0 ? x : -x; } }";
        const string modern = "record P(int X); class B { int[] M() { int[] a = [1, 2]; return a; } }";
        foreach (var (code, version) in new[] { (legacy, LanguageVersion.CSharp7_3), (modern, LanguageVersion.Latest) })
        {
            var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(version));
            var errors = tree.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
            if (errors != 0) throw new InvalidOperationException($"{version} 파싱 오류 {errors}건");
        }
        return "C# 7.3·최신 파싱 성공";
    }

    private static string ScriptDom()
    {
        const string sql = "SET ANSI_NULLS ON\nGO\nCREATE PROCEDURE dbo.P AS SELECT 1\nGO\nSET NOCOUNT ON\n";
        var script = SqlBatchSplitter.Split(sql, 160);
        if (script.LexErrors.Count != 0) throw new InvalidOperationException("SQL 어휘 오류: " + script.LexErrors[0]);
        if (script.Batches.Count != 3 || script.ObjectBatches.Count != 1)
            throw new InvalidOperationException($"배치 {script.Batches.Count}개·객체 {script.ObjectBatches.Count}개 (기대: 3·1)");
        return "배치 3개·객체 1개";
    }

    private static string Cp949RoundTrip()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp949 = Encoding.GetEncoding(949);
        var back = cp949.GetString(cp949.GetBytes("한글"));
        return back == "한글" ? "CP949 왕복 성공" : throw new InvalidOperationException($"왕복 결과가 다릅니다: {back}");
    }

    private static string Resources()
    {
        _ = PromptResources.LoadDefault();
        _ = QwenChatTemplate.LoadDefault();
        return "프롬프트·채팅 템플릿 로드 성공";
    }
}
