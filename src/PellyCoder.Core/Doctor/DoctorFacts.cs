namespace PellyCoder.Core.Doctor;

/// <summary>Row names and <c>Facts</c> keys shared by <see cref="DoctorService"/> (producer) and <see cref="DoctorSanitizer"/> (whitelist), so a rename cannot empty a fact silently.</summary>
public static class DoctorFacts
{
    public const string OllamaServer = "Ollama 서버";
    public const string Model = "모델";
    public const string Renderer = "렌더러";
    public const string TemplateMatch = "템플릿 일치";
    public const string TokenizerMatch = "토크나이저 일치";
    public const string Latency = "응답 시간";
    public const string Pelly = "pelly";
    public const string Settings = "설정";
    public const string DotnetSdk = ".NET SDK";
    public const string MsBuild = "MSBuild (레거시)";
    public const string TargetingPack = "타겟팅 팩";
    public const string ServerSettings = "서버 설정 확인 필요";
    public const string Sql = "SQL 연결";

    public const string OllamaVersion = "ollamaVersion";
    public const string LatencyMs = "latencyMs";
    public const string Loaded = "loaded";
    public const string TtftMs = "ttftMs";
    public const string Match = "match";
    public const string DotnetVersion = "dotnetVersion";
    public const string Found = "found";
    public const string PellyVersion = "pellyVersion";
    public const string TokenizerHash = "tokenizerHash";
    public const string CanViewDefinition = "canViewDefinition";
    public const string ServerMajor = "serverMajor";
    public const string FrameworkVersion = "frameworkVersion";
    public const string Installed = "installed";
}
