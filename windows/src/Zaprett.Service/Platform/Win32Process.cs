using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Zaprett.Service.Native;

namespace Zaprett.Service.Platform;

/// <summary>
/// A child process started with CreateProcessW: argv escaped by <see cref="WindowsCommandLine"/>, no shell, no window,
/// created suspended and put into a Job Object before it runs, so it and all its children die with the job.
/// Only its own stdin/stdout/stderr pipe ends are inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST).
/// </summary>
public sealed unsafe class Win32Process : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly bool _ownsJob;
    private readonly RegisteredWaitHandle _wait;
    private readonly ProcessWaitHandle _waitHandle;
    private int _waitReleased;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Pid { get; }
    public JobObject Job { get; }
    public Stream StdOut { get; }
    public Stream StdErr { get; }
    /// <summary>Completes with the exit code when the process ends.</summary>
    public Task<int> Exited => _exited.Task;

    private Win32Process(SafeProcessHandle process, int pid, JobObject job, bool ownsJob, Stream stdout, Stream stderr)
    {
        _process = process;
        Pid = pid;
        Job = job;
        _ownsJob = ownsJob;
        StdOut = stdout;
        StdErr = stderr;
        _waitHandle = new ProcessWaitHandle(process);
        _wait = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) =>
        {
            try
            {
                Win32.GetExitCodeProcess(_process, out uint code);
                _exited.TrySetResult(unchecked((int)code));
            }
            catch (ObjectDisposedException)
            {
                _exited.TrySetResult(-1);
            }
            ReleaseWait();
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>Starts a process. With <paramref name="job"/> null the process gets its own job, owned by this object.</summary>
    public static Win32Process Start(string executable, IReadOnlyList<string> args, string? workingDirectory = null, JobObject? job = null)
    {
        string commandLine = WindowsCommandLine.Build(executable, args);
        if (commandLine.Length >= 32767)
            throw new ArgumentException("command line is longer than 32767 characters", nameof(args));

        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        bool ownsJob = job is null;
        job ??= new JobObject();
        nint attrList = 0;
        nint handles = 0;
        try
        {
            nint[] inherit = [stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(),
                stderr.ClientSafePipeHandle.DangerousGetHandle()];
            nint size = 0;
            Win32.InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!Win32.InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed");
            handles = Marshal.AllocHGlobal(IntPtr.Size * inherit.Length);
            Marshal.Copy(inherit, 0, handles, inherit.Length);
            if (!Win32.UpdateProcThreadAttribute(attrList, 0, Win32.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles,
                    IntPtr.Size * inherit.Length, 0, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed");

            var si = new Win32.STARTUPINFOEXW();
            si.StartupInfo.cb = sizeof(Win32.STARTUPINFOEXW);
            si.StartupInfo.dwFlags = Win32.STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = inherit[0];
            si.StartupInfo.hStdOutput = inherit[1];
            si.StartupInfo.hStdError = inherit[2];
            si.lpAttributeList = attrList;

            Win32.PROCESS_INFORMATION pi;
            char[] cmd = new char[commandLine.Length + 1];
            commandLine.CopyTo(0, cmd, 0, commandLine.Length);
            fixed (char* pcmd = cmd)
            {
                if (!Win32.CreateProcess(executable, pcmd, 0, 0, true,
                        Win32.CREATE_SUSPENDED | Win32.CREATE_NO_WINDOW | Win32.EXTENDED_STARTUPINFO_PRESENT | Win32.CREATE_UNICODE_ENVIRONMENT,
                        0, workingDirectory, ref si, out pi))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess failed: " + executable);
            }
            var process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            try
            {
                job.Assign(process);
            }
            catch
            {
                // never let a process run outside the job
                Win32.TerminateProcess(process, 1);
                Win32.CloseHandle(pi.hThread);
                process.Dispose();
                throw;
            }
            Win32.ResumeThread(pi.hThread);
            Win32.CloseHandle(pi.hThread);
            return new Win32Process(process, pi.dwProcessId, job, ownsJob, stdout, stderr);
        }
        catch
        {
            stdout.Dispose();
            stderr.Dispose();
            if (ownsJob)
                job.Dispose();
            throw;
        }
        finally
        {
            // our copies of the child's pipe ends: once the child has them (or failed), they only keep pipes open
            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            if (attrList != 0)
            {
                Win32.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (handles != 0)
                Marshal.FreeHGlobal(handles);
        }
    }

    /// <summary>Kills the process and everything else in its job.</summary>
    public void Kill() => Job.Terminate();

    private void ReleaseWait()
    {
        if (Interlocked.Exchange(ref _waitReleased, 1) == 0)
            _waitHandle.Dispose();
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        ReleaseWait();
        StdOut.Dispose();
        StdErr.Dispose();
        if (_ownsJob)
            Job.Dispose();   // KILL_ON_JOB_CLOSE ends leftovers
        _process.Dispose();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            bool added = false;
            process.DangerousAddRef(ref added);
            _process = process;
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }

        private readonly SafeProcessHandle _process;

        protected override void Dispose(bool explicitDisposing)
        {
            base.Dispose(explicitDisposing);
            _process.DangerousRelease();
        }
    }
}
