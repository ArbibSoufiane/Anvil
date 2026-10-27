using ArbiPache.Core.Agent;
using ArbiPache.Core.Logging;
using ArbiPache.Core.Paths;
using Serilog;

namespace ArbiPache.Agent;

internal static class Program
{
    /// <summary>
    /// <c>--child</c> (default): started by the GUI. <c>--service</c>: under the Windows service manager (Phase 7).
    /// Exit code 3: another Agent is already running.
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        var paths = AppPaths.FromBaseDirectory();
        Logging.Configure(paths, "agent");
        var options = new AgentOptions(AgentPipe.Name, AgentPipe.Mutex, RunningAsService: args.Contains("--service"));
        try
        {
            return await new AgentHost(options, paths).RunAsync(CancellationToken.None);
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
}
