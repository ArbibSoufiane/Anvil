using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Paths;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Services;

namespace ArbiPache.Elevator.Handlers;

/// <summary>
/// service-install and service-uninstall. The service is ArbiPacheAgent: delayed automatic start, NT SERVICE\ArbiPacheAgent,
/// restarted after 5 s, 30 s and 60 s when it fails (counts reset after a day), and the installing user may start and stop it.
/// </summary>
internal static unsafe class ServiceHandler
{
    private const string DisplayName = "ArbiPache Agent";
    private const string Description = "Runs ArbiPache's web server, PHP pools and database.";
    private const uint OneDay = 86_400;

    // System and administrators keep full control, signed-in and service users may read its status, and the
    // installing user may also start and stop it.
    private const string Access = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    public static ElevatedResult Install(ServiceInstallRequest request, AppPaths paths)
    {
        if (ServiceFolderCheck.Problem(paths.Root, FolderPermissions.Access(paths.Root), request.ControlUserSid) is { } problem)
            return new ElevatedResult(false, problem);

        using var manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT | PInvoke.SC_MANAGER_CREATE_SERVICE);
        if (manager.IsInvalid) return Failed("open the Windows service list");

        var binary = $"\"{request.AgentExe}\" --service";
        var service = PInvoke.CreateService(manager, AclPlanner.ServiceName, DisplayName, PInvoke.SERVICE_ALL_ACCESS,
            ENUM_SERVICE_TYPE.SERVICE_WIN32_OWN_PROCESS, SERVICE_START_TYPE.SERVICE_AUTO_START, SERVICE_ERROR.SERVICE_ERROR_NORMAL,
            binary, null, null, AclPlanner.ServiceAccount, null);
        try
        {
            if (service.IsInvalid && (WIN32_ERROR)Marshal.GetLastPInvokeError() == WIN32_ERROR.ERROR_SERVICE_EXISTS)
            {
                service.Dispose();
                service = PInvoke.OpenService(manager, AclPlanner.ServiceName, PInvoke.SERVICE_ALL_ACCESS);
                if (service.IsInvalid) return Failed("open the ArbiPache service");
                if (!PInvoke.ChangeServiceConfig(service, ENUM_SERVICE_TYPE.SERVICE_WIN32_OWN_PROCESS, SERVICE_START_TYPE.SERVICE_AUTO_START,
                        SERVICE_ERROR.SERVICE_ERROR_NORMAL, binary, null, null, AclPlanner.ServiceAccount, null, DisplayName))
                    return Failed("update the ArbiPache service");
            }
            else if (service.IsInvalid)
            {
                return Failed("register the ArbiPache service");
            }

            return Configure(service, request.ControlUserSid) ?? new ElevatedResult(true, null);
        }
        finally
        {
            service.Dispose();
        }
    }

    public static ElevatedResult Uninstall()
    {
        using var manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT);
        if (manager.IsInvalid) return Failed("open the Windows service list");
        using var service = PInvoke.OpenService(manager, AclPlanner.ServiceName, PInvoke.SERVICE_ALL_ACCESS);
        if (service.IsInvalid)
        {
            return (WIN32_ERROR)Marshal.GetLastPInvokeError() == WIN32_ERROR.ERROR_SERVICE_DOES_NOT_EXIST
                ? new ElevatedResult(true, null)
                : Failed("open the ArbiPache service");
        }

        // A service that does not stop now is removed once it does.
        PInvoke.ControlService(service, PInvoke.SERVICE_CONTROL_STOP, out _);
        if (!PInvoke.DeleteService(service) && (WIN32_ERROR)Marshal.GetLastPInvokeError() != WIN32_ERROR.ERROR_SERVICE_MARKED_FOR_DELETE)
            return Failed("remove the ArbiPache service");
        return new ElevatedResult(true, null);
    }

    private static ElevatedResult? Configure(SafeHandle service, string controlUserSid)
    {
        fixed (char* text = Description)
        {
            var description = new SERVICE_DESCRIPTIONW { lpDescription = text };
            if (!PInvoke.ChangeServiceConfig2W(service, SERVICE_CONFIG.SERVICE_CONFIG_DESCRIPTION, &description))
                return Failed("describe the ArbiPache service");
        }

        var delayed = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = true };
        if (!PInvoke.ChangeServiceConfig2W(service, SERVICE_CONFIG.SERVICE_CONFIG_DELAYED_AUTO_START_INFO, &delayed))
            return Failed("delay the ArbiPache service's start");

        var sid = new SERVICE_SID_INFO { dwServiceSidType = PInvoke.SERVICE_SID_TYPE_UNRESTRICTED };
        if (!PInvoke.ChangeServiceConfig2W(service, SERVICE_CONFIG.SERVICE_CONFIG_SERVICE_SID_INFO, &sid))
            return Failed("give the ArbiPache service its own account");

        var restarts = stackalloc SC_ACTION[]
        {
            new() { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = 5_000 },
            new() { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = 30_000 },
            new() { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = 60_000 },
        };
        var failure = new SERVICE_FAILURE_ACTIONSW { dwResetPeriod = OneDay, cActions = 3, lpsaActions = restarts };
        if (!PInvoke.ChangeServiceConfig2W(service, SERVICE_CONFIG.SERVICE_CONFIG_FAILURE_ACTIONS, &failure))
            return Failed("set the ArbiPache service to restart when it fails");

        var access = new RawSecurityDescriptor(Access + $"(A;;CCLCSWRPWPDTLOCRRC;;;{controlUserSid})");
        var bytes = new byte[access.BinaryLength];
        access.GetBinaryForm(bytes, 0);
        fixed (byte* descriptor = bytes)
        {
            if (!PInvoke.SetServiceObjectSecurity(service, OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION, new PSECURITY_DESCRIPTOR(descriptor)))
                return Failed("let you start and stop the ArbiPache service");
        }

        return null;
    }

    private static ElevatedResult Failed(string what) => new(false, $"Windows did not let ArbiPache {what}: {new Win32Exception().Message}");
}
