using ArbiPache.App.SingleInstance;
using Avalonia;
using Avalonia.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ArbiPache.App;

internal static class Program
{
    /// <summary>Set by the shell; runs on the UI thread when a second launch asks this one to show itself.</summary>
    internal static Action<string[]>? Activated { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        using var guard = new InstanceGuard();
        var pipeName = ActivationPipe.ForCurrentUser();
        if (!guard.TryAcquire())
        {
            if (ActivationClient.SendAsync(pipeName, args, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult()) return 0;
            PInvoke.MessageBox(HWND.Null,
                "ArbiPache is already running but not responding. Close it from Task Manager and try again.",
                "ArbiPache", MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONWARNING);
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
}
