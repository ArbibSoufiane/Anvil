using ArbiPache.Core.Paths;
using Serilog;

namespace ArbiPache.Core.Logging;

public static class Logging
{
    /// <summary>Daily rolling log at data\logs\arbipache\&lt;component&gt;-.log, 14 files kept.</summary>
    public static void Configure(AppPaths paths, string component)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.Logs, "arbipache", $"{component}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();
    }
}
