using Anvil.Core.Agent;

namespace Anvil.App.ViewModels;

/// <summary>What the person reads when the Agent refuses a certificate the client sent.</summary>
internal static class CertificateMessages
{
    public static string For(Exception e) => AgentErrors.CertificateCode(e) switch
    {
        "key-mismatch" => "The private key does not belong to this certificate.",
        "bad-password" => "The password does not open this .pfx file.",
        // Expired, names-not-covered and unreadable messages carry the date, names or file they are about.
        _ => e.Message,
    };
}
