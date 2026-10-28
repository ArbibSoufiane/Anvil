using System.Text.Json;
using Anvil.Core.Certificates;
using Anvil.Core.IO;
using Anvil.Core.Paths;
using Serilog;

namespace Anvil.Agent;

/// <summary>
/// Renews certificates shortly after the Agent starts, then every night at 03:00 plus a minute picked once for this
/// copy of Anvil (kept in data\renewal.json), so many servers do not ask Let's Encrypt at the same moment.
/// A run where a renewal failed is followed by another six hours later.
/// </summary>
public sealed class RenewalWorker(Func<CancellationToken, Task<RenewalReport>> renew, AppPaths paths, TimeProvider time)
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);
    private const int Hour = 3;

    private sealed record State(int Minute);

    public static DateTimeOffset NextRun(DateTimeOffset nowLocal, int minute, DateTimeOffset? retryAt)
    {
        var today = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, Hour, minute, 0, nowLocal.Offset);
        var slot = today > nowLocal ? today : today.AddDays(1);
        return retryAt is { } retry && retry < slot ? retry : slot;
    }

    public static int MinuteFor(AppPaths paths)
    {
        var file = Path.Combine(paths.Data, "renewal.json");
        try
        {
            if (File.Exists(file) && JsonFiles.Read<State>(file) is { Minute: >= 0 and < 60 } state) return state.Minute;
        }
        catch (JsonException)
        {
            // Unreadable: pick a new minute below.
        }

        var minute = Random.Shared.Next(60);
        JsonFiles.Write(file, new State(minute));
        return minute;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var minute = MinuteFor(paths);
        var next = time.GetLocalNow() + StartupDelay;
        try
        {
            while (true)
            {
                var wait = next - time.GetLocalNow();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, time, ct);
                DateTimeOffset? retryAt = null;
                try
                {
                    var report = await renew(ct);
                    if (report.Renewed.Count > 0) Log.Information("Renewed certificates for {Sites}", string.Join(", ", report.Renewed));
                    if (report.Failed.Count > 0) retryAt = time.GetLocalNow() + RetryAfter;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Error(e, "Certificate renewal run failed");
                    retryAt = time.GetLocalNow() + RetryAfter;
                }

                next = NextRun(time.GetLocalNow(), minute, retryAt);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The Agent is stopping.
        }
    }
}
