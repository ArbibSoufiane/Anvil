namespace Anvil.Core.Preflight;

public enum Severity { Info, Warning, Blocking }

/// <summary>One problem found before Apache starts. Fix is the user-facing next step, when there is one.</summary>
public sealed record PreflightFinding(string Id, Severity Severity, string Message, string? Fix);
