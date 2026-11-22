using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zapretiks.Infrastructure;

/// <summary>Private, non-inheritable Windows job. Closing its handle ends only this launch and its children.</summary>
internal sealed class WindowsStrategyJob : IDisposable
{
    private readonly SafeFileHandle job;
    private SafeProcessHandle? engine;

    private WindowsStrategyJob(SafeFileHandle job) => this.job = job;

    public bool EngineIsAlive => engine is not null && WaitForSingleObject(engine, 0) == 258;

    public static WindowsStrategyJob Launch(string directory, string strategy)
    {
        var handle = CreateJobObjectW(0, null);
        try
        {
            Check(!handle.IsInvalid);
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            Check(SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()));
            LaunchInJob(handle, directory, strategy);
            return new(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // Attach at process creation. Even a batch that immediately uses START cannot escape the ownership check.
    private static void LaunchInJob(SafeFileHandle handle, string directory, string strategy)
    {
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        Check(size != 0);
        var attributes = Marshal.AllocHGlobal(checked((nint)size));
        var handleSlot = Marshal.AllocHGlobal(IntPtr.Size);
        var initialized = false;
        try
        {
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            initialized = true;
            Marshal.WriteIntPtr(handleSlot, handle.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, 0x2000D, handleSlot, (nuint)IntPtr.Size, 0, 0));
            var startup = new StartupInfoEx { Size = Marshal.SizeOf<StartupInfoEx>(), Attributes = attributes };
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            // The strategy name was allowlisted. Keep the directory out of the command text entirely.
            var commandLine = new StringBuilder($"\"{cmd}\" /d /v:off /c set \"NO_UPDATE_CHECK=1\"&&\".\\{strategy}\"");
            const uint extendedStartupInfo = 0x80000, createNoWindow = 0x08000000;
            Check(CreateProcessW(cmd, commandLine, 0, 0, false, extendedStartupInfo | createNoWindow, 0,
                directory, ref startup, out var process));
            CloseHandle(process.Thread);
            CloseHandle(process.Process);
        }
        finally
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(handleSlot);
            Marshal.FreeHGlobal(attributes);
            GC.KeepAlive(handle);
        }
    }

    // A process handle pins the kernel object even if the numeric PID is subsequently reused.
    public bool FindEngine(string expectedPath)
    {
        const int capacity = 512;
        var bytes = 8 + capacity * IntPtr.Size;
        var ids = Marshal.AllocHGlobal(bytes);
        try
        {
            Check(QueryInformationJobObject(job, 3, ids, bytes, 0)); // JobObjectBasicProcessIdList
            int count = Marshal.ReadInt32(ids, 4);
            for (var index = 0; index < count; index++)
            {
                int pid = checked((int)Marshal.ReadIntPtr(ids, 8 + index * IntPtr.Size));
                var process = OpenProcess(0x101000, false, pid); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
                var matched = false;
                try
                {
                    if (process.IsInvalid || !IsProcessInJob(process, job, out bool member) || !member)
                        continue;
                    var path = new StringBuilder(32768);
                    int length = path.Capacity;
                    if (!QueryFullProcessImageNameW(process, 0, path, ref length) ||
                        !expectedPath.Equals(path.ToString(), StringComparison.OrdinalIgnoreCase) ||
                        WaitForSingleObject(process, 0) != 258)
                        continue;
                    engine = process;
                    matched = true;
                    return true;
                }
                finally
                {
                    if (!matched)
                        process.Dispose();
                }
            }

            if (count == 0)
                throw new InvalidOperationException("Стратегия завершилась до запуска zapret. Проверьте файлы сборки.");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ids);
        }
    }

    public void Stop()
    {
        Check(TerminateJobObject(job, 0));
        if (engine is not null && WaitForSingleObject(engine, 4000) != 0)
            throw new InvalidOperationException("Windows ещё завершает zapret. Обновите состояние.");
    }

    public void Dispose()
    {
        job.Dispose();
        engine?.Dispose();
    }

    private static void Check(bool ok)
    {
        if (!ok)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, Width, Height, XCount, YCount, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, StandardInput, StandardOutput, StandardError, Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, int bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, nint info, int bytes, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint key, nint value, nuint size,
        nint previousValue, nint returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processSecurity,
        nint threadSecurity, bool inheritHandles, uint flags, nint environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
