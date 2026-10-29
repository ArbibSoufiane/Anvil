using System.Security.AccessControl;
using System.Security.Principal;

namespace Anvil.Core.Database;

/// <summary>
/// The one entry Anvil adds to a database service's permissions so the user can start and stop it without an
/// administrator prompt each time. Works on the binary security descriptor Windows returns for the service.
/// </summary>
public static class ServiceAccess
{
    // SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS | SERVICE_START | SERVICE_STOP | SERVICE_INTERROGATE
    public const int StartStop = 0x0001 | 0x0004 | 0x0010 | 0x0020 | 0x0080;
    private const int StartAndStopBits = 0x0010 | 0x0020;

    /// <summary>The descriptor with the user allowed to start and stop; null when the user may already.</summary>
    public static byte[]? Grant(byte[] descriptor, SecurityIdentifier user)
    {
        var sd = new RawSecurityDescriptor(descriptor, 0);
        var dacl = sd.DiscretionaryAcl ?? new RawAcl(GenericAcl.AclRevision, 1);
        if (dacl.OfType<CommonAce>().Any(a => a.AceQualifier == AceQualifier.AccessAllowed && a.SecurityIdentifier == user
                                               && (a.AccessMask & StartAndStopBits) == StartAndStopBits))
            return null;
        dacl.InsertAce(dacl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, StartStop, user, false, null));
        sd.DiscretionaryAcl = dacl;
        return Binary(sd);
    }

    /// <summary>The descriptor without the entry <see cref="Grant"/> added; null when it has none.</summary>
    public static byte[]? Revoke(byte[] descriptor, SecurityIdentifier user)
    {
        var sd = new RawSecurityDescriptor(descriptor, 0);
        if (sd.DiscretionaryAcl is not { } dacl) return null;
        var removed = false;
        for (var i = dacl.Count - 1; i >= 0; i--)
        {
            if (dacl[i] is CommonAce { AceQualifier: AceQualifier.AccessAllowed, AccessMask: StartStop } a && a.SecurityIdentifier == user)
            {
                dacl.RemoveAce(i);
                removed = true;
            }
        }
        return removed ? Binary(sd) : null;
    }

    private static byte[] Binary(RawSecurityDescriptor sd)
    {
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        return bytes;
    }
}
