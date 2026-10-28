namespace Anvil.Core.Certificates;

/// <summary>
/// A certificate the client sent cannot be used. Code is one of key-mismatch, expired, names-not-covered, bad-password, unreadable;
/// the message is written for the person importing it.
/// </summary>
public sealed class CertificateImportException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
