using Anvil.Core.Agent;
using Anvil.Core.Elevation;
using Anvil.Core.Logging;
using Anvil.Core.Paths;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Anvil.Agent;

internal static class Program
{
    /// <summary>
    /// <c>--child</c> (default): started by the GUI. <c>--service</c>: under the Windows service manager.
    /// Exit code 3: another Agent is already running.
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        var paths = AppPaths.FromBaseDirectory();
        Logging.Configure(paths, "agent");
        var options = AgentOptions.FromArgs(args);
        try
        {
            return options.RunningAsService ? await RunServiceAsync(options, paths) : await new AgentHost(options, paths).RunAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Agent crashed");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>Windows starts and stops the Agent; a shutdown asked for over the pipe stops the service.</summary>
    private static async Task<int> RunServiceAsync(AgentOptions options, AppPaths paths)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = paths.Root });
        builder.Services.AddLogging();
        builder.Services.AddWindowsService(o => o.ServiceName = AclPlanner.ServiceName);
        // Stopping httpd, the PHP pools and the database can take longer than the default 30 s.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));
        builder.Services.AddHostedService(s => new AgentService(options, paths, s.GetRequiredService<IHostApplicationLifetime>()));
        using var host = builder.Build();
        await host.RunAsync();
        return Environment.ExitCode;
    }

    private sealed class AgentService(AgentOptions options, AppPaths paths, IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Environment.ExitCode = await new AgentHost(options, paths).RunAsync(stoppingToken);
            lifetime.StopApplication();
        }
    }
}
