using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.IO;
using Windows.Win32;
using Windows.Win32.Security;

namespace Anvil.Elevator.Handlers;

/// <summary>
/// db-service-access: the user who wrote the request (its file's owner) may start and stop one database service, or no
/// longer may. db-port: the port in the settings file that service starts with.
/// </summary>
internal static class DbServiceHandler
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ReadControl = 0x0002_0000;
    private const uint WriteDac = 0x0004_0000;

    // Latin-1 maps every byte to one char, so the lines Anvil does not change are written back byte for byte.
    private static readonly Encoding Bytes = Encoding.Latin1;

    public static ElevatedResult Run(DbServiceAccessRequest request, string requestPath)
    {
        var user = (SecurityIdentifier)new FileInfo(requestPath).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
        using var manager = PInvoke.OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid) throw new Win32Exception();
        using var service = PInvoke.OpenService(manager, request.Service, ReadControl | WriteDac);
        if (service.IsInvalid) throw new Win32Exception();

        var current = Descriptor(service);
        var next = request.Grant ? ServiceAccess.Grant(current, user) : ServiceAccess.Revoke(current, user);
        if (next is not null) SetDescriptor(service, next);
        return new ElevatedResult(true, null);
    }

    public static ElevatedResult Run(DbPortRequest request)
    {
        var (command, error) = RequestValidator.DbServiceError(request.Service, WindowsServices.ImagePath);
        if (error is not null) return new ElevatedResult(false, error);
        // The file often sits in a folder the user can write to, so a temporary file beside it could be a planted link.
        PinnedFile.Rewrite(command!.DefaultsFile!, Bytes, text => DbPortFile.Write(text, request.Port));
        return new ElevatedResult(true, null);
    }

    private static unsafe byte[] Descriptor(SafeHandle service)
    {
        PInvoke.QueryServiceObjectSecurity(service, (uint)OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION, default, 0, out var needed);
        var buffer = new byte[needed];
        fixed (byte* p = buffer)
        {
            if (!PInvoke.QueryServiceObjectSecurity(service, (uint)OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION, new PSECURITY_DESCRIPTOR(p), needed, out _))
                throw new Win32Exception();
        }
        return buffer;
    }

    private static unsafe void SetDescriptor(SafeHandle service, byte[] descriptor)
    {
        fixed (byte* p = descriptor)
        {
            if (!PInvoke.SetServiceObjectSecurity(service, OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION, new PSECURITY_DESCRIPTOR(p)))
                throw new Win32Exception();
        }
    }
}
