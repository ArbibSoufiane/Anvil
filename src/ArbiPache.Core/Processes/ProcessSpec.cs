using System.Net.Sockets;

namespace ArbiPache.Core.Processes;

public enum RestartPolicy { Never, Always }

public enum ProcessState { Starting, Running, Restarting, Stopped, Failed }

/// <param name="LogFile">stdout and stderr are appended here.</param>
/// <param name="StdinListenSocket">
/// A listening socket the child inherits as stdin and accept()s on, the way php-cgi workers share one pool port.
/// Such a child gets no stdout or stderr (php-cgi only speaks FastCGI then), so only supervisor lines reach <paramref name="LogFile"/>.
/// The caller owns the socket and keeps it open across restarts.
/// </param>
public sealed record ProcessSpec(string Name, string Exe, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env,
    string WorkingDir, string LogFile, RestartPolicy Restart = RestartPolicy.Always, Socket? StdinListenSocket = null);

/// <param name="Restarts">Restarts after crashes. Clean exits (recycles) are not counted.</param>
public sealed record ProcessStatus(string Name, ProcessState State, int? Pid, DateTimeOffset? StartedAt, int Restarts, string? LastError);
