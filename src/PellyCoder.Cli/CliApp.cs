using PellyCoder.Cli.Commands;
using PellyCoder.Core;
using Spectre.Console.Cli;

namespace PellyCoder.Cli;

public static class CliApp
{
    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName("pelly");
        config.SetApplicationVersion(PellyInfo.Version);
        config.AddCommand<CsCommand>("cs").WithDescription("C# 메서드 하나를 요청대로 수정합니다.");
        config.AddCommand<DoctorCommand>("doctor").WithDescription("Ollama 서버·모델·토크나이저 환경을 점검합니다.");
    }
}
