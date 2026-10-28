using Anvil.Core.Agent;
using Anvil.Core.Logging;
using Anvil.Core.Paths;
using Serilog;

namespace Anvil.Agent;

internal static class Program
{
    /// <summary>Started by the window as its child. Exit code 3: another Agent is already running.</summary>
    private static async Task<int> Main(string[] args)
    {
        var paths = AppPaths.FromBaseDirectory();
        Logging.Configure(paths, "agent");
        var options = AgentOptions.FromArgs(args);
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
