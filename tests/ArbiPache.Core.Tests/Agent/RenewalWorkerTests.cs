using ArbiPache.Agent;
using ArbiPache.Core.Certificates;
using ArbiPache.Core.Paths;
using Microsoft.Extensions.Time.Testing;

namespace ArbiPache.Core.Tests.Agent;

public sealed class RenewalWorkerTests : IDisposable
{
    private static readonly TimeSpan Plus4 = TimeSpan.FromHours(4);
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-renew-" + Guid.NewGuid().ToString("N")));

    public RenewalWorkerTests() => _paths.EnsureCreated();

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, Plus4);

    [Fact]
    public void Next_run_is_three_oclock_plus_the_minute()
    {
        Assert.Equal(At(27, 3, 17), RenewalWorker.NextRun(At(27, 1, 0), minute: 17, retryAt: null));
        Assert.Equal(At(28, 3, 17), RenewalWorker.NextRun(At(27, 3, 17), minute: 17, retryAt: null));
        Assert.Equal(At(28, 3, 17), RenewalWorker.NextRun(At(27, 14, 0), minute: 17, retryAt: null));
    }

    [Fact]
    public void Failed_renewal_retries_after_six_hours()
    {
        Assert.Equal(At(27, 20, 0), RenewalWorker.NextRun(At(27, 14, 0), minute: 17, retryAt: At(27, 20, 0)));
        Assert.Equal(At(28, 3, 17), RenewalWorker.NextRun(At(27, 23, 0), minute: 17, retryAt: At(28, 5, 0)));
    }

    [Fact]
    public void Renewal_minute_is_kept_between_runs()
    {
        var first = RenewalWorker.MinuteFor(_paths);

        Assert.InRange(first, 0, 59);
        Assert.Equal(first, RenewalWorker.MinuteFor(_paths));
        Assert.True(File.Exists(Path.Combine(_paths.Data, "renewal.json")));
    }

    [Fact]
    public async Task Runs_shortly_after_start_then_daily_and_retries_failures()
    {
        var time = new FakeTimeProvider(At(27, 14, 0).ToUniversalTime());
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("plus4", Plus4, "plus4", "plus4"));
        var runs = new List<DateTimeOffset>();
        var ran = new SemaphoreSlim(0);
        var failNext = true;
        var worker = new RenewalWorker(_ =>
        {
            runs.Add(time.GetLocalNow());
            var report = failNext ? new RenewalReport([], ["shop.example.com"]) : new RenewalReport(["shop.example.com"], []);
            failNext = false;
            ran.Release();
            return Task.FromResult(report);
        }, _paths, time);
        using var cts = new CancellationTokenSource();
        var minute = RenewalWorker.MinuteFor(_paths);

        var loop = worker.RunAsync(cts.Token);
        await AdvanceUntilRunAsync(time, ran, RenewalWorker.StartupDelay);
        await AdvanceUntilRunAsync(time, ran, RenewalWorker.RetryAfter);
        await AdvanceUntilRunAsync(time, ran, At(28, 3, minute) - time.GetLocalNow());
        await cts.CancelAsync();
        await loop;

        Assert.Equal([At(27, 14, 0) + RenewalWorker.StartupDelay, At(27, 14, 0) + RenewalWorker.StartupDelay + RenewalWorker.RetryAfter, At(28, 3, minute)],
            runs);
    }

    private static async Task AdvanceUntilRunAsync(FakeTimeProvider time, SemaphoreSlim ran, TimeSpan by)
    {
        // Let the loop reach its delay before the clock moves.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        time.Advance(by);
        Assert.True(await ran.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
