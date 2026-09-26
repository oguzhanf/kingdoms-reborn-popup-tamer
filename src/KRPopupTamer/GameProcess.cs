using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KRPopupTamer;

// Read/write access to the running game process.
sealed class GameProcess : IDisposable
{
    public const string ProcessName = "PrototypeCity-Win64-Shipping";
    const uint VmOperation = 0x8, VmRead = 0x10, VmWrite = 0x20, QueryInformation = 0x400;
    readonly nint _handle;
    public Process Process { get; }
    public int Pid => Process.Id;
    public ulong Base { get; }
    public bool HasExited => Process.HasExited;

    GameProcess(Process process, nint handle, ulong moduleBase) => (Process, _handle, Base) = (process, handle, moduleBase);

    // Returns null when the game is not running (or is still starting / already exiting).
    public static GameProcess? TryOpen()
    {
        var process = Process.GetProcessesByName(ProcessName).FirstOrDefault();
        if (process == null) return null;
        var handle = Native.OpenProcess(VmOperation | VmRead | VmWrite | QueryInformation, false, process.Id);
        if (handle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (process.HasExited) return null;
            throw new Win32Exception(error, "Cannot open the game process. If the game runs as administrator, run this app as administrator too");
        }
        try
        {
            return new GameProcess(process, handle, (ulong)process.MainModule!.BaseAddress);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Native.CloseHandle(handle);
            return null;
        }
    }

    public (uint Stamp, uint SizeOfImage) ImageIdentity()
    {
        var peHeader = Base + BitConverter.ToUInt32(Read(Base + 0x3c, 4));
        return (BitConverter.ToUInt32(Read(peHeader + 8, 4)), BitConverter.ToUInt32(Read(peHeader + 0x18 + 0x38, 4)));
    }

    public byte[] Read(ulong address, int length)
    {
        var buffer = new byte[length];
        if (!Native.ReadProcessMemory(_handle, (nint)address, buffer, length, out var read) || read != length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadProcessMemory 0x{address:x}");
        return buffer;
    }

    public ulong U64(ulong address) => BitConverter.ToUInt64(Read(address, 8));
    public int I32(ulong address) => BitConverter.ToInt32(Read(address, 4));

    public ulong Ptr(ulong address)
    {
        var value = U64(address);
        if (value < 0x10000 || value > 0x7FFF_FFFF_FFFF) throw new InvalidOperationException($"null pointer at 0x{address:x}");
        return value;
    }

    // MSVC std::vector {first, last, end} read in one call; rejects torn or implausible values.
    public (ulong First, int Count) Vector(ulong address, int stride, int maxCount)
    {
        var raw = Read(address, 24);
        ulong first = BitConverter.ToUInt64(raw, 0), last = BitConverter.ToUInt64(raw, 8), end = BitConverter.ToUInt64(raw, 16);
        if (first == 0 && last == 0) return (0, 0);
        var bytes = last - first;
        if (first > last || last > end || bytes % (ulong)stride != 0 || bytes / (ulong)stride > (ulong)maxCount)
            throw new InvalidOperationException($"implausible vector at 0x{address:x}");
        return (first, (int)(bytes / (ulong)stride));
    }

    // For memory we allocated as read/write/execute and that no game thread executes yet.
    public void WriteData(ulong address, byte[] bytes)
    {
        if (!Native.WriteProcessMemory(_handle, (nint)address, bytes, bytes.Length, out var written) || written != bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteProcessMemory 0x{address:x}");
        Native.FlushInstructionCache(_handle, (nint)address, bytes.Length);
    }

    // Writes game code with every game thread suspended, so no thread can execute a half-written instruction.
    // Returns false (and writes nothing) if a thread could not be stopped or is stopped inside one of the guard
    // ranges [Start, End); the caller retries later. A failed write puts back what it already changed.
    public bool WriteCode(IReadOnlyList<(ulong Address, byte[] Bytes)> writes, IReadOnlyList<(ulong Start, ulong End)> guards)
    {
        var threads = SuspendAllThreads();
        if (threads == null) return false;
        try
        {
            if (threads.Any(t => guards.Any(g => t.Rip == null || (t.Rip >= g.Start && t.Rip < g.End))))
                return false;
            var changed = new List<(ulong Address, byte[] Original)>();
            try
            {
                foreach (var (address, bytes) in writes)
                {
                    changed.Add((address, Read(address, bytes.Length)));
                    WriteProtected(address, bytes);
                }
            }
            catch
            {
                foreach (var (address, original) in changed)
                    try { WriteProtected(address, original); } catch (Win32Exception) { }
                throw;
            }
        }
        finally
        {
            foreach (var (thread, _) in threads)
            {
                Native.ResumeThread(thread);
                Native.CloseHandle(thread);
            }
        }
        foreach (var (address, bytes) in writes)
            if (!Read(address, bytes.Length).SequenceEqual(bytes))
                throw new InvalidOperationException($"Read-back mismatch at 0x{address:x}");
        return true;
    }

    void WriteProtected(ulong address, byte[] bytes)
    {
        if (!Native.VirtualProtectEx(_handle, (nint)address, bytes.Length, Native.PageExecuteReadWrite, out var oldProtect))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtectEx");
        try
        {
            if (!Native.WriteProcessMemory(_handle, (nint)address, bytes, bytes.Length, out var written) || written != bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory");
        }
        finally
        {
            Native.VirtualProtectEx(_handle, (nint)address, bytes.Length, oldProtect, out _);
        }
        Native.FlushInstructionCache(_handle, (nint)address, bytes.Length);
    }

    // Candidate addresses within +-2 GB below the game module (64 KB allocation granularity), nearest first.
    public IEnumerable<ulong> NearModuleCandidates()
    {
        const ulong step = 0x10000, maxDistance = 0x6000_0000;
        for (ulong offset = step; offset < maxDistance; offset += step) yield return Base - offset;
    }

    public bool IsCommitted(ulong address) =>
        Native.VirtualQueryEx(_handle, (nint)address, out var info, Marshal.SizeOf<Native.MemoryBasicInformation>()) != 0
        && info.State == Native.MemCommit;

    // Allocates read/write/execute memory near the game module, so rel32 jumps can reach it.
    public ulong AllocateNearModule(int size)
    {
        foreach (var candidate in NearModuleCandidates())
        {
            var address = Native.VirtualAllocEx(_handle, (nint)candidate, size, Native.MemCommitReserve, Native.PageExecuteReadWrite);
            if (address != 0) return (ulong)address;
        }
        throw new InvalidOperationException("No free memory near the game module");
    }

    // Suspends every thread of the game and returns its handle and instruction pointer (null if unreadable).
    // Returns null (with nothing left suspended) if a live thread cannot be stopped or threads keep appearing.
    List<(nint Handle, ulong? Rip)>? SuspendAllThreads()
    {
        const uint access = Native.ThreadSuspendResume | Native.ThreadGetContext | Native.ThreadQueryLimitedInformation;
        const int contextSize = 1232, contextFlagsOffset = 0x30, ripOffset = 0xF8, contextControl = 0x100001; // x64 CONTEXT
        const int errorInvalidParameter = 87;
        var suspended = new List<(nint Handle, ulong? Rip)>();
        var seen = new HashSet<int>();
        var contextMemory = Marshal.AllocHGlobal(contextSize + 16);
        var context = (contextMemory + 15) & ~(nint)15; // GetThreadContext requires 16-byte alignment
        void ResumeAll()
        {
            foreach (var (thread, _) in suspended) { Native.ResumeThread(thread); Native.CloseHandle(thread); }
            suspended.Clear();
        }
        try
        {
            // Repeat until a pass finds no new thread, so threads created meanwhile are stopped too.
            for (var pass = 0; pass < 5; pass++)
            {
                Process.Refresh();
                var fresh = Process.Threads.Cast<ProcessThread>().Select(t => t.Id).Where(seen.Add).ToList();
                if (fresh.Count == 0) return suspended;
                foreach (var id in fresh)
                {
                    var thread = Native.OpenThread(access, false, id);
                    if (thread == 0)
                    {
                        if (Marshal.GetLastWin32Error() == errorInvalidParameter) continue; // thread already exited
                        ResumeAll();
                        return null;
                    }
                    // A thread id can be reused by another process once the original thread exits.
                    if (Native.GetProcessIdOfThread(thread) != Pid) { Native.CloseHandle(thread); continue; }
                    if (Native.SuspendThread(thread) == uint.MaxValue) { Native.CloseHandle(thread); ResumeAll(); return null; }
                    // SuspendThread is asynchronous; GetThreadContext returns only once the thread has actually stopped.
                    Marshal.WriteInt32(context, contextFlagsOffset, contextControl);
                    var rip = Native.GetThreadContext(thread, context) ? (ulong?)(ulong)Marshal.ReadInt64(context, ripOffset) : null;
                    suspended.Add((thread, rip));
                }
            }
            ResumeAll();
            return null;
        }
        catch
        {
            ResumeAll();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(contextMemory);
        }
    }
    public void Dispose()
    {
        Native.CloseHandle(_handle);
        Process.Dispose();
    }
}

static class Native
{
    public const uint PageExecuteReadWrite = 0x40, MemCommitReserve = 0x3000, MemCommit = 0x1000;
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32", SetLastError = true)] public static extern nint VirtualQueryEx(nint process, nint address, out MemoryBasicInformation info, int length);
    public const uint ThreadSuspendResume = 0x2, ThreadGetContext = 0x8, ThreadQueryLimitedInformation = 0x800;
    [DllImport("kernel32", SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] public static extern nint OpenThread(uint access, bool inherit, int tid);
    [DllImport("kernel32", SetLastError = true)] public static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", SetLastError = true)] public static extern uint SuspendThread(nint thread);
    [DllImport("kernel32", SetLastError = true)] public static extern uint ResumeThread(nint thread);
    [DllImport("kernel32", SetLastError = true)] public static extern int GetProcessIdOfThread(nint thread);
    [DllImport("kernel32", SetLastError = true)] public static extern bool GetThreadContext(nint thread, nint context);
    [DllImport("kernel32", SetLastError = true)] public static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint read);
    [DllImport("kernel32", SetLastError = true)] public static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint written);
    [DllImport("kernel32", SetLastError = true)] public static extern bool VirtualProtectEx(nint process, nint address, nint size, uint newProtect, out uint oldProtect);
    [DllImport("kernel32", SetLastError = true)] public static extern nint VirtualAllocEx(nint process, nint address, nint size, uint type, uint protect);
    [DllImport("kernel32", SetLastError = true)] public static extern bool FlushInstructionCache(nint process, nint address, nint size);
}
