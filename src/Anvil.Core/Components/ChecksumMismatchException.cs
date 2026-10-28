namespace Anvil.Core.Components;

public sealed class ChecksumMismatchException(string expected, string actual)
    : Exception($"Download checksum mismatch: expected SHA-256 {expected}, got {actual}. The file was deleted.")
{
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}
