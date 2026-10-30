using System.Collections.ObjectModel;
using System.Globalization;
using Anvil.App.Services;
using Anvil.Core.Redis;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>Where the Redis page stands; each shows its own card.</summary>
public enum RedisPhase { Checking, WslMissing, NoUbuntu, NotInstalled, Installing, InstallFailed, Stopped, Starting, Running, NotAnswering }

/// <summary>One step of a guide on the Redis page; <see cref="Command"/>, when set, gets a Copy button.</summary>
public sealed record RedisStep(string Text, string? Command = null)
{
    public bool HasCommand => Command is not null;
}

/// <summary>One line of the connection details, with a Copy button.</summary>
public sealed record RedisCredential(string Label, string Value);

/// <summary>
/// The Redis page: Redis runs in WSL's Ubuntu. Checks WSL and Ubuntu, installs redis-server with one click, starts it and
/// tests that Windows can reach it. When something is missing or fails, it shows how to do that step by hand.
/// </summary>
public sealed partial class RedisViewModel : ObservableObject
{
    public const string Host = "127.0.0.1";
    public const string WslInstallUrl = "https://learn.microsoft.com/windows/wsl/install";
    public const string ManualInstallUrl = "https://redis.io/docs/latest/operate/oss_and_stack/install/install-redis/install-redis-on-windows/";
    public const string RedisInsightUrl = "https://redis.io/insight/";
    private const int LogLines = 300;

    private readonly WslRedis _redis;
    private readonly ITextClipboard _clipboard;
    private readonly Action<string> _openUrl;
    private readonly Action<Action> _ui;
    private readonly Func<string, int, string?, CancellationToken, Task<RedisPing>> _ping;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private bool _checked;
    private int _port = WslRedis.DefaultPort;
    private string? _password;

    public RedisViewModel(WslRedis redis, ITextClipboard clipboard, Action<string>? openUrl = null, Action<Action>? ui = null,
        Func<string, int, string?, CancellationToken, Task<RedisPing>>? ping = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _redis = redis;
        _clipboard = clipboard;
        _openUrl = openUrl ?? EditorLauncher.OpenUrl;
        _ui = ui ?? (a => Dispatcher.UIThread.Post(a));
        _ping = ping ?? ((host, port, password, ct) => RedisProbe.PingAsync(host, port, password, TimeSpan.FromSeconds(2), ct));
        _delay = delay ?? Task.Delay;
    }

    public static IReadOnlyList<RedisStep> WslSteps { get; } =
    [
        new("Open PowerShell as administrator and run this. It installs WSL and Ubuntu together:", "wsl --install -d Ubuntu"),
        new("Restart Windows when it asks."),
        new("Open Ubuntu from the Start menu once and choose a user name and password."),
        new("Come back here and press Check again."),
    ];

    public static IReadOnlyList<RedisStep> UbuntuSteps { get; } =
    [
        new("Open PowerShell and run this to add Ubuntu to WSL:", "wsl --install -d Ubuntu"),
        new("Open Ubuntu from the Start menu once and choose a user name and password."),
        new("Come back here and press Check again."),
    ];

    public static IReadOnlyList<RedisStep> ManualSteps { get; } =
    [
        new("Open Ubuntu from the Start menu, or run this in PowerShell:", "wsl -d Ubuntu"),
        new("Install Redis:", "sudo apt-get update && sudo apt-get install -y redis-server"),
        new("Give it a password (choose your own):", $"echo 'requirepass your-password' | sudo tee -a {WslRedis.Conf}"),
        new("Start it:", "sudo service redis-server restart"),
        new("Check it answers; it prints PONG:", "redis-cli -a your-password ping"),
        new("Come back here and press Check again."),
    ];

    public static IReadOnlyList<RedisStep> DebugSteps { get; } =
    [
        new("Check that Redis runs in Ubuntu; it should say it is running:", "wsl -d Ubuntu -u root service redis-server status"),
        new("Ask Redis from inside Ubuntu; it prints PONG when it runs:", "wsl -d Ubuntu -u root redis-cli -a your-password ping"),
        new("Read Redis' own log for the reason it stopped:", "wsl -d Ubuntu -u root tail -n 30 /var/log/redis/redis-server.log"),
        new("See whether another program on Windows holds the port (the last number is its process id):", "netstat -ano | findstr :6379"),
        new("Windows reaches Redis through WSL's localhost forwarding. If %UserProfile%\\.wslconfig has localhostForwarding=false, " +
            "remove that line, then restart WSL:", "wsl --shutdown"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsWslMissing), nameof(IsNoUbuntu), nameof(IsNotInstalled), nameof(IsInstalling),
        nameof(IsInstallFailed), nameof(IsStopped), nameof(IsStarting), nameof(IsRunning), nameof(IsNotAnswering), nameof(IsInstalled),
        nameof(ShowLog), nameof(IsBusy))]
    public partial RedisPhase Phase { get; set; } = RedisPhase.Checking;

    public bool IsChecking => Phase == RedisPhase.Checking;
    public bool IsWslMissing => Phase == RedisPhase.WslMissing;
    public bool IsNoUbuntu => Phase == RedisPhase.NoUbuntu;
    public bool IsNotInstalled => Phase == RedisPhase.NotInstalled;
    public bool IsInstalling => Phase == RedisPhase.Installing;
    public bool IsInstallFailed => Phase == RedisPhase.InstallFailed;
    public bool IsStopped => Phase == RedisPhase.Stopped;
    public bool IsStarting => Phase == RedisPhase.Starting;
    public bool IsRunning => Phase == RedisPhase.Running;
    public bool IsNotAnswering => Phase == RedisPhase.NotAnswering;
    public bool IsInstalled => Phase is RedisPhase.Stopped or RedisPhase.Starting or RedisPhase.Running or RedisPhase.NotAnswering;
    public bool IsBusy => Phase is RedisPhase.Checking or RedisPhase.Installing or RedisPhase.Starting;
    public bool ShowLog => Phase is RedisPhase.Installing or RedisPhase.InstallFailed && InstallLog.Count > 0;

    /// <summary>The Ubuntu distribution Redis runs in, such as Ubuntu-24.04.</summary>
    [ObservableProperty]
    public partial string? Distro { get; set; }

    /// <summary>What went wrong, in a sentence, or what wsl.exe said.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string? Detail { get; set; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageOk { get; set; }

    /// <summary>What the install printed, newest last.</summary>
    public ObservableCollection<string> InstallLog { get; } = [];

    /// <summary>Host, port, password, URL and a Laravel .env block, once Redis is installed.</summary>
    public ObservableCollection<RedisCredential> Credentials { get; } = [];

    /// <summary>The first visit checks; later visits keep what the page shows until Check again.</summary>
    public Task LoadAsync(CancellationToken ct = default) => _checked ? Task.CompletedTask : CheckAsync(ct);

    [RelayCommand]
    private async Task CheckAsync(CancellationToken ct = default)
    {
        _checked = true;
        Phase = RedisPhase.Checking;
        Message = null;
        Detail = null;
        WslRedisState state;
        try
        {
            state = await _redis.DetectAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Serilog.Log.Warning(e, "Checking WSL for Redis failed");
            state = new WslRedisState(WslKind.Missing, Detail: e.Message);
        }

        Distro = state.Distro;
        Detail = state.Detail;
        ShowCredentials(state);
        Phase = state switch
        {
            { Kind: WslKind.Missing } => RedisPhase.WslMissing,
            { Kind: WslKind.NoUbuntu } => RedisPhase.NoUbuntu,
            { RedisInstalled: false } => RedisPhase.NotInstalled,
            _ => RedisPhase.Stopped,
        };
        if (Phase != RedisPhase.Stopped) return;

        var ping = await _ping(Host, _port, _password, ct);
        if (ping.Ok)
        {
            Phase = RedisPhase.Running;
            Say(ping.Message, ok: true);
        }
    }

    [RelayCommand]
    private async Task InstallAsync(CancellationToken ct = default)
    {
        if (Distro is null) return;
        InstallLog.Clear();
        Message = null;
        Detail = null;
        Phase = RedisPhase.Installing;
        WslResult result;
        try
        {
            result = await _redis.InstallAsync(Distro, line => _ui(() => AddLog(line)), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Serilog.Log.Warning(e, "Installing Redis in {Distro} failed", Distro);
            result = new WslResult(-1, e.Message);
        }

        if (!result.Ok)
        {
            Serilog.Log.Warning("Installing Redis in {Distro} failed (exit {Code}): {Output}", Distro, result.ExitCode, result.Output);
            Detail = $"The install stopped (exit code {result.ExitCode}). The last lines it printed are below.";
            if (InstallLog.Count == 0) foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) AddLog(line.TrimEnd('\r'));
            Phase = RedisPhase.InstallFailed;
            OnPropertyChanged(nameof(ShowLog));
            return;
        }

        // The password now lives in redis.conf; read it back rather than trusting the script.
        var state = await _redis.DetectAsync(ct);
        ShowCredentials(state);
        await StartCoreAsync(ct);
    }

    [RelayCommand]
    private Task StartAsync(CancellationToken ct = default) => StartCoreAsync(ct);

    [RelayCommand]
    private async Task StopAsync(CancellationToken ct = default)
    {
        if (Distro is null) return;
        var result = await _redis.StopAsync(Distro, ct);
        Phase = RedisPhase.Stopped;
        if (result.Ok) Say("Redis stopped.", ok: true);
        else Say($"Stopping Redis failed: {result.Output.Trim()}", ok: false);
    }

    [RelayCommand]
    private async Task TestConnectionAsync(CancellationToken ct = default)
    {
        var ping = await _ping(Host, _port, _password, ct);
        Phase = ping.Ok ? RedisPhase.Running : RedisPhase.NotAnswering;
        Detail = ping.Ok ? null : ping.Message;
        if (ping.Ok) Say(ping.Message, ok: true);
        else Message = null;
    }

    [RelayCommand]
    private void OpenUrl(string url) => _openUrl(url);

    [RelayCommand]
    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        await _clipboard.SetTextAsync(text);
        Say("Copied.", ok: true);
    }

    // Starts Redis with a keeper beside it, then waits up to ten seconds for it to answer.
    private async Task StartCoreAsync(CancellationToken ct)
    {
        if (Distro is null) return;
        Phase = RedisPhase.Starting;
        Message = null;
        Detail = null;
        try
        {
            _redis.Start(Distro);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Serilog.Log.Warning(e, "Starting Redis in {Distro} failed", Distro);
            Detail = e.Message;
            Phase = RedisPhase.NotAnswering;
            return;
        }

        RedisPing ping = new(false, "");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            ping = await _ping(Host, _port, _password, ct);
            if (ping.Ok) break;
            await _delay(TimeSpan.FromSeconds(1), ct);
        }

        if (ping.Ok)
        {
            Phase = RedisPhase.Running;
            Say(ping.Message, ok: true);
        }
        else
        {
            Serilog.Log.Warning("Redis in {Distro} does not answer: {Message}", Distro, ping.Message);
            Detail = ping.Message;
            Phase = RedisPhase.NotAnswering;
        }
    }

    private void ShowCredentials(WslRedisState state)
    {
        _port = state.Port;
        _password = state.Password;
        Credentials.Clear();
        if (!state.RedisInstalled) return;
        var port = state.Port.ToString(CultureInfo.InvariantCulture);
        Credentials.Add(new("Host", Host));
        Credentials.Add(new("Port", port));
        Credentials.Add(new("Password", state.Password ?? "(none)"));
        Credentials.Add(new("URL", state.Password is { } p ? $"redis://default:{p}@{Host}:{port}" : $"redis://{Host}:{port}"));
        Credentials.Add(new("Laravel .env", $"REDIS_CLIENT=phpredis\nREDIS_HOST={Host}\nREDIS_PASSWORD={state.Password ?? "null"}\nREDIS_PORT={port}"));
    }

    private void AddLog(string line)
    {
        InstallLog.Add(line);
        while (InstallLog.Count > LogLines) InstallLog.RemoveAt(0);
        OnPropertyChanged(nameof(ShowLog));
    }

    private void Say(string message, bool ok)
    {
        Message = message;
        MessageOk = ok;
    }
}
