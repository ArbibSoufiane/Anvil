namespace Anvil.Core.Agent;

/// <summary>A new user name and password for a password-protected site. Only its bcrypt hash is stored.</summary>
public sealed record SiteSecrets(string? User, string? Password);
