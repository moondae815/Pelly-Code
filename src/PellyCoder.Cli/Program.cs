using PellyCoder.Core;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("pelly");
    config.SetApplicationVersion(PellyInfo.Version);
});
return app.Run(args);
