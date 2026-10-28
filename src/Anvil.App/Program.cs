using System.ComponentModel;
using System.Diagnostics;
using Anvil.App.SingleInstance;
using Anvil.Core.Logging;
using Anvil.Core.Paths;
using Anvil.Core.Update;
using Avalonia;
using Avalonia.Threading;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Anvil.App;

internal static class Program
{
    /// <summary>Set by the shell; runs on the UI thread when a second launch asks this one to show itself.</summary>
    internal static Action<string[]>? Activated { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // The staged copy of a new version, started by the old window: no window and no instance mutex of its own.
        if (ApplyUpdateArgs.Parse(args) is { } update) return ApplyUpdate(update);

        using var guard = new InstanceGuard();
        var pipeName = ActivationPipe.ForCurrentUser();
        if (!guard.TryAcquire())
        {
            if (ActivationClient.SendAsync(pipeName, args, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult()) return 0;
            PInvoke.MessageBox(HWND.Null,
                "Anvil is already running but not responding. Close it from Task Manager and try again.",
                "Anvil", MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONWARNING);
            return 1;
        }

        var server = new ActivationServer(pipeName, OnActivateAsync);
        server.StartAsync().GetAwaiter().GetResult();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();

    private static Task OnActivateAsync(string[] args)
    {
        Dispatcher.UIThread.Post(() => Activated?.Invoke(args));
        return Task.CompletedTask;
    }

    /// <summary>Replaces the program files once the old window has closed, then opens whichever version is in place.</summary>
    private static int ApplyUpdate(ApplyUpdateArgs update)
    {
        Logging.Configure(new AppPaths(update.Target).AppLogs, "update");
        try
        {
            Updater.Swap(update.Staged, update.Target, update.Pid);
            Log.Information("Replaced the program files in {Target} from {Staged}", update.Target, update.Staged);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // A failed swap has put the previous files back, so the old version opens below.
            Log.Error(e, "The update was not applied");
            Warn($"The update was not applied. {e.Message}");
        }

        var exe = Path.Combine(update.Target, "Anvil.exe");
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = update.Target });
            return 0;
        }
        catch (Win32Exception e)
        {
            Log.Error(e, "Starting {Exe} after the update failed", exe);
            Warn($"Anvil could not start {exe} ({e.Message}). Start it yourself from that folder.");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void Warn(string text) =>
        PInvoke.MessageBox(HWND.Null, text, "Anvil update", MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONWARNING);
}
