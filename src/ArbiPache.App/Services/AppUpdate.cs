using System.Diagnostics;
using ArbiPache.Core;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Update;
using Serilog;

namespace ArbiPache.App.Services;

/// <summary>ArbiPache's own update, as the Settings page drives it. Nothing checks in the background.</summary>
public interface IAppUpdate
{
    /// <summary>Why this copy cannot update itself, or null.</summary>
    string? CannotUpdate { get; }

    /// <summary>The newer release, or null when this is the latest.</summary>
    Task<Release?> CheckAsync(CancellationToken ct);

    /// <summary>
    /// Downloads and checks the release, stops the Agent and starts the staged ArbiPache.exe, which replaces the program
    /// files once this window has closed.
    /// </summary>
    /// <returns>Null once the window is closing for the update; otherwise why not, with the Agent running again.</returns>
    Task<string?> InstallAsync(Release release, IProgress<long>? progress, CancellationToken ct);
}

/// <param name="launch">Starts the staged ArbiPache.exe with the given arguments.</param>
/// <param name="exit">Closes this window.</param>
public sealed class AppUpdate(
    AppPaths paths,
    IAgentApi api,
    IAgentLink link,
    Func<CancellationToken, Task<Release?>> check,
    Func<Release, IProgress<long>?, CancellationToken, Task<string>> stage,
    Action<string, IReadOnlyList<string>> launch,
    Action exit) : IAppUpdate
{
    private static readonly string[] BuildFolders = [@"\bin\Debug\", @"\bin\Release\"];

    /// <summary>Checks GitHub releases; downloads with <paramref name="http"/>.</summary>
    public static AppUpdate FromGitHub(AppPaths paths, IAgentApi api, IAgentLink link, HttpClient http, Action exit)
    {
        var feed = new ReleaseFeed(http);
        var updater = new Updater(paths, http);
        return new AppUpdate(paths, api, link, ct => feed.CheckAsync(BuildInfo.Version, ct), updater.StageAsync, Launch, exit);
    }

    // A build output folder holds no release layout to swap, and the next build would overwrite it anyway.
    public string? CannotUpdate =>
        BuildFolders.Any(f => (paths.Root + Path.DirectorySeparatorChar).Contains(f, StringComparison.OrdinalIgnoreCase))
            ? "This copy of ArbiPache was built from source, so it does not update itself. Build the new version instead."
            : null;

    public Task<Release?> CheckAsync(CancellationToken ct) => check(ct);

    public async Task<string?> InstallAsync(Release release, IProgress<long>? progress, CancellationToken ct)
    {
        string staged;
        try
        {
            staged = await stage(release, progress, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Staging ArbiPache {Version} failed", release.Version);
            return $"ArbiPache {release.Version} was not installed: {e.Message}";
        }

        // Held until this window exits, so no Agent starts again on the old files.
        var hold = link.HoldStart();
        try
        {
            try
            {
                await api.ShutdownAsync(stopServices: true, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The pipe closes as the Agent exits.
                Log.Information("The Agent closed the connection while shutting down: {Message}", e.Message);
            }
            await link.WhenAgentGoneAsync(ct);
            launch(Path.Combine(staged, "ArbiPache.exe"), new ApplyUpdateArgs(staged, paths.Root, Environment.ProcessId).ToArgs());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Handing over to ArbiPache {Version} failed", release.Version);
            hold.Dispose();
            try
            {
                await link.ConnectAsync(CancellationToken.None);
            }
            catch (Exception again) when (again is not OperationCanceledException)
            {
                Log.Warning(again, "Reconnecting after the failed update failed");
            }
            return $"ArbiPache {release.Version} was not installed: {e.Message} ArbiPache {BuildInfo.Version} keeps running.";
        }

        Log.Information("Handing over to ArbiPache {Version} in {Staged}", release.Version, staged);
        exit();
        return null;
    }

    private static void Launch(string exe, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var _ = Process.Start(start) ?? throw new InvalidOperationException($"Windows did not start {exe}.");
    }
}
