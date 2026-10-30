using System.Globalization;
using System.Security.Cryptography;

namespace Anvil.Core.Redis;

/// <summary>Where WSL stands on this PC, for Redis.</summary>
public enum WslKind
{
    /// <summary>WSL is not installed, or does not work.</summary>
    Missing,

    /// <summary>WSL works but has no Ubuntu distribution.</summary>
    NoUbuntu,

    /// <summary>An Ubuntu distribution answers.</summary>
    Ready,
}

/// <summary>What <see cref="WslRedis.DetectAsync"/> found. <see cref="Detail"/> is what wsl.exe said when something is wrong.</summary>
public sealed record WslRedisState(WslKind Kind, string? Distro = null, bool RedisInstalled = false, int Port = WslRedis.DefaultPort,
    string? Password = null, string? Detail = null);

/// <summary>
/// Redis in WSL's Ubuntu: finds the distribution, installs redis-server with apt as root (no sudo password needed), sets a
/// password and starts it. Windows reaches it on 127.0.0.1 because WSL forwards localhost. The password lives in
/// /etc/redis/redis.conf only, and is read back from there.
/// </summary>
public sealed class WslRedis(IWsl wsl)
{
    public const int DefaultPort = 6379;
    public const string Conf = "/etc/redis/redis.conf";

    private static readonly TimeSpan Quick = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Install = TimeSpan.FromMinutes(15);

    public async Task<WslRedisState> DetectAsync(CancellationToken ct)
    {
        if (!wsl.Exists) return new(WslKind.Missing, Detail: "wsl.exe is not on this PC.");

        var list = await wsl.RunAsync(["--list", "--quiet"], null, null, Quick, ct);
        if (!list.Ok)
        {
            // With no distribution at all the list fails too, while --status still answers.
            var status = await wsl.RunAsync(["--status"], null, null, Quick, ct);
            return new(status.Ok ? WslKind.NoUbuntu : WslKind.Missing, Detail: Clean(list.Output));
        }

        var distro = list.Output.Split('\n').Select(l => l.Trim().Trim('\0'))
            .FirstOrDefault(n => n.StartsWith("Ubuntu", StringComparison.OrdinalIgnoreCase));
        if (distro is null) return new(WslKind.NoUbuntu);

        var probe = await RootAsync(distro, """
            . /etc/os-release; echo "id=$ID"
            if command -v redis-server >/dev/null 2>&1; then echo installed=yes; fi
            if [ -f /etc/redis/redis.conf ]; then grep -E '^(port|requirepass) ' /etc/redis/redis.conf; fi
            true
            """, null, Quick, ct);
        if (!probe.Ok) return new(WslKind.NoUbuntu, distro, Detail: Clean(probe.Output));

        var lines = probe.Output.Split('\n').Select(l => l.Trim()).ToList();
        if (!lines.Contains("id=ubuntu")) return new(WslKind.NoUbuntu, distro, Detail: $"{distro} does not report itself as Ubuntu.");
        var port = lines.Select(l => Value(l, "port")).FirstOrDefault(v => v is not null) is { } p
            && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : DefaultPort;
        var password = lines.Select(l => Value(l, "requirepass")).FirstOrDefault(v => v is not null);
        return new(WslKind.Ready, distro, lines.Contains("installed=yes"), port, password is null ? null : Unquote(password));
    }

    /// <summary>
    /// Installs redis-server, gives it a password unless it has one, and starts it. Each line apt prints goes to
    /// <paramref name="onLine"/>.
    /// </summary>
    public Task<WslResult> InstallAsync(string distro, Action<string>? onLine, CancellationToken ct) =>
        RootAsync(distro, InstallScript(NewPassword()), onLine, Install, ct);

    public static string InstallScript(string password) => $$"""
        set -e
        export DEBIAN_FRONTEND=noninteractive
        echo '> apt-get update'
        apt-get update
        echo '> apt-get install -y redis-server'
        apt-get install -y redis-server
        if ! grep -q '^requirepass ' {{Conf}}; then
          echo '> setting a password in {{Conf}}'
          echo 'requirepass {{password}}' >> {{Conf}}
        fi
        service redis-server restart
        """;

    /// <summary>
    /// Starts redis-server and keeps a wsl.exe running beside it, as WSL stops a distribution nothing on Windows uses.
    /// The keeper ends by itself once Redis stops.
    /// </summary>
    public void Start(string distro) => wsl.StartDetached(["--distribution", distro, "--user", "root", "--exec", "sh", "-s"], Wrap(KeeperScript));

    public const string KeeperScript = """
        service redis-server start >/dev/null 2>&1
        while service redis-server status >/dev/null 2>&1; do sleep 30; done
        """;

    public Task<WslResult> StopAsync(string distro, CancellationToken ct) => RootAsync(distro, "service redis-server stop", null, Quick, ct);

    private Task<WslResult> RootAsync(string distro, string script, Action<string>? onLine, TimeSpan timeout, CancellationToken ct) =>
        wsl.RunAsync(["--distribution", distro, "--user", "root", "--exec", "sh", "-s"], Wrap(script), onLine, timeout, ct);

    // sh reads the script from standard input; one { } block is read whole before it runs, so nothing in it can read the
    // rest of the script as its own input.
    private static string Wrap(string script) => "{\n" + script.ReplaceLineEndings("\n") + "\n} </dev/null\n";

    /// <summary>Letters and digits only, so it needs no escaping in redis.conf, a URL or a shell.</summary>
    public static string NewPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 24);
    }

    private static string? Value(string line, string key) =>
        line.StartsWith(key + " ", StringComparison.Ordinal) ? line[(key.Length + 1)..].Trim() : null;

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'' ? value[1..^1] : value;

    private static string Clean(string output) => output.Replace("\0", "", StringComparison.Ordinal).Trim();
}
