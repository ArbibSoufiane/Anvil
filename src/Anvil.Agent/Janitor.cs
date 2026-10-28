using Anvil.Core.IO;
using Anvil.Core.Paths;
using Serilog;

namespace Anvil.Agent;

/// <summary>
/// Once a day, starting a few minutes after the Agent: deletes logs nobody wrote for 14 days (Apache's rotatelogs starts a
/// new file every day) and mode-switch snapshots beyond the newest 30. Database dumps keep their own count.
/// </summary>
public sealed class Janitor(AppPaths paths, TimeProvider time)
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);
    public static readonly TimeSpan LogAge = TimeSpan.FromDays(14);
    public const int KeepSnapshots = 30;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, time, ct);
            while (true)
            {
                Clean();
                await Task.Delay(Every, time, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The Agent is stopping.
        }
    }

    public void Clean()
    {
        var logs = new DirectoryInfo(paths.Logs);
        if (logs.Exists)
        {
            // Links are skipped, not followed: only Anvil's own logs are deleted.
            var files = logs.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint });
            foreach (var file in Retention.Expired(files, time.GetUtcNow(), LogAge)) Delete(() => File.Delete(file), file);
        }

        foreach (var snapshot in Retention.BeyondNewest(new BackupService(paths, time).List(), KeepSnapshots))
            Delete(() => Directory.Delete(snapshot, recursive: true), snapshot);
    }

    private static void Delete(Action delete, string path)
    {
        try
        {
            delete();
            Log.Information("Deleted {Path}", path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Could not delete {Path}: {Message}", path, e.Message);
        }
    }
}
