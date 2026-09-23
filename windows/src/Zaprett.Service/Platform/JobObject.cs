using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Zaprett.Service.Native;

namespace Zaprett.Service.Platform;

/// <summary>A Job Object with KILL_ON_JOB_CLOSE: closing it (or the service dying) ends every process in it.</summary>
public sealed unsafe class JobObject : IDisposable
{
    private readonly SafeFileHandle _handle;

    public JobObject()
    {
        _handle = Win32.CreateJobObject(0, null);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed");
        var info = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | Win32.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        if (!Win32.SetInformationJobObject(_handle, Win32.JobObjectExtendedLimitInformation, &info, sizeof(Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            int err = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(err, "SetInformationJobObject failed");
        }
    }

    public void Assign(SafeProcessHandle process)
    {
        if (!Win32.AssignProcessToJobObject(_handle, process))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed");
    }

    /// <summary>Kills every process in the job (the whole tree started from it).</summary>
    public void Terminate(uint exitCode = 1)
    {
        if (!_handle.IsClosed)
            Win32.TerminateJobObject(_handle, exitCode);
    }

    /// <summary>Process ids currently in the job.</summary>
    public IReadOnlyList<int> GetProcessIds()
    {
        // JOBOBJECT_BASIC_PROCESS_ID_LIST: uint assigned, uint listed, ULONG_PTR ids[]
        const int max = 256;
        int size = 8 + max * IntPtr.Size;
        byte* buf = stackalloc byte[size];
        if (!Win32.QueryInformationJobObject(_handle, Win32.JobObjectBasicProcessIdList, buf, size, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryInformationJobObject failed");
        uint listed = *(uint*)(buf + 4);
        var ids = new List<int>((int)listed);
        nuint* p = (nuint*)(buf + 8);
        for (int i = 0; i < listed; i++)
            ids.Add((int)p[i]);
        return ids;
    }

    public void Dispose() => _handle.Dispose();
}
