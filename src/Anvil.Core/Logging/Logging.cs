using Anvil.Core.Paths;
using Serilog;

namespace Anvil.Core.Logging;

public static class Logging
{
    /// <summary>
    /// Daily rolling log at &lt;folder&gt;\&lt;component&gt;-.log, 14 files kept: <see cref="AppPaths.AgentLogs"/> for the
    /// Agent, <see cref="AppPaths.AppLogs"/> for the window.
    /// </summary>
    public static void Configure(string folder, string component)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(folder, $"{component}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();
    }
}
