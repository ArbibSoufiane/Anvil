using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace Anvil.Core.Processes;

/// <summary>
/// A Windows job with KILL_ON_JOB_CLOSE: when the agent exits for any reason, including a crash,
/// Windows ends every process assigned to the job, so no orphaned httpd or php-cgi stays behind.
/// </summary>
public sealed class JobObject : IDisposable
{
    private readonly SafeFileHandle _handle;

    public unsafe JobObject()
    {
        _handle = PInvoke.CreateJobObject(null, (string?)null);
        if (_handle.IsInvalid) throw new Win32Exception();

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!PInvoke.SetInformationJobObject(new HANDLE(_handle.DangerousGetHandle()), JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
            throw new Win32Exception();
    }

    public void Assign(Process p)
    {
        if (!PInvoke.AssignProcessToJobObject(_handle, p.SafeHandle)) throw new Win32Exception();
    }

    public void Dispose() => _handle.Dispose();
}
