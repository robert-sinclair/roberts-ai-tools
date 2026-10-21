using rMic.Core;
using rMic.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

AppPaths.EnsureDataDirectory();

var logger = new Logger(AppPaths.LogFile);
var config = ServiceConfig.LoadOrCreate(AppPaths.ConfigFile, logger);

if (args.Length > 0 && Cli.IsVerb(args[0]))
    return Cli.Run(args, logger, config);

logger.EchoToConsole = args.Contains("--console", StringComparer.OrdinalIgnoreCase);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    // Services start with %SystemRoot%\System32 as the working directory.
    Args = Array.Empty<string>(),
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService(options => options.ServiceName = "rMic");
builder.Services.AddSingleton(logger);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<MicSwitch>();
builder.Services.AddHostedService<rMicWorker>();

try
{
    builder.Build().Run();
    return 0;
}
catch (Exception ex)
{
    logger.Error("Host terminated unexpectedly", ex);
    return 1;
}
