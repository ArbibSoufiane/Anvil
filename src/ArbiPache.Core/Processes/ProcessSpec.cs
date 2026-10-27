namespace ArbiPache.Core.Processes;

public enum RestartPolicy { Never, Always }

public enum ProcessState { Starting, Running, Restarting, Stopped, Failed }

/// <param name="LogFile">stdout and stderr are appended here.</param>
public sealed record ProcessSpec(string Name, string Exe, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env,
    string WorkingDir, string LogFile, RestartPolicy Restart = RestartPolicy.Always);

/// <param name="Restarts">Restarts after crashes. Clean exits (recycles) are not counted.</param>
public sealed record ProcessStatus(string Name, ProcessState State, int? Pid, DateTimeOffset? StartedAt, int Restarts, string? LastError);
