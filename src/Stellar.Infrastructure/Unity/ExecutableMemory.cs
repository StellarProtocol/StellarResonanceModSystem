using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Minimal Win32 surface for native code patching (<see cref="Il2CppInteropFastPath"/>): checked reads of
/// arbitrary process memory, a never-freed executable buffer, and an atomic compare-and-swap of an 8-byte
/// pointer slot that lives in executable memory. Every signature is blittable (no marshalling stubs).
/// </summary>
internal static unsafe class ExecutableMemory
{
    private const uint MemCommit = 0x1000, MemReserve = 0x2000;
    private const uint PageNoAccess = 0x01, PageReadWrite = 0x04, PageExecuteRead = 0x20, PageExecuteReadWrite = 0x40;
    private const uint PageGuard = 0x100;
    private const uint ExecuteMask = 0x10 | 0x20 | 0x40 | 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;   // SIZE_T; the ushort above is padded to 8 by sequential layout
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern int VirtualProtect(IntPtr addr, UIntPtr size, uint newProtect, uint* oldProtect);

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern UIntPtr VirtualQuery(IntPtr addr, MemoryBasicInformation* info, UIntPtr length);

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern int FlushInstructionCache(IntPtr process, IntPtr addr, UIntPtr size);

    /// <summary>True when every byte of [<paramref name="addr"/>, +<paramref name="len"/>) is committed and
    /// readable (and, when <paramref name="mustExecute"/>, executable). Walks region by region.</summary>
    public static bool IsAccessible(IntPtr addr, int len, bool mustExecute)
    {
        if (addr == IntPtr.Zero || len <= 0) return false;
        long p = (long)addr, end = p + len;
        while (p < end)
        {
            MemoryBasicInformation mbi;
            if (VirtualQuery((IntPtr)p, &mbi, (UIntPtr)(uint)sizeof(MemoryBasicInformation)) == UIntPtr.Zero) return false;
            if (mbi.State != MemCommit) return false;
            uint prot = mbi.Protect;
            if (prot == 0 || (prot & PageNoAccess) != 0 || (prot & PageGuard) != 0) return false;
            if (mustExecute && (prot & ExecuteMask) == 0) return false;
            long regionEnd = (long)mbi.BaseAddress + (long)mbi.RegionSize;
            if (regionEnd <= p) return false;
            p = regionEnd;
        }
        return true;
    }

    /// <summary>Copy <paramref name="len"/> bytes from <paramref name="addr"/>, or null if any of it is not
    /// readable — never faults on a bad pointer.</summary>
    public static byte[]? TryRead(IntPtr addr, int len)
    {
        if (!IsAccessible(addr, len, mustExecute: false)) return null;
        var buf = new byte[len];
        Marshal.Copy(addr, buf, 0, len);
        return buf;
    }

    /// <summary>Allocate a fresh page, copy <paramref name="code"/> in, and seal it read+execute. The page is
    /// deliberately never freed: once a live detour points at it, it must outlive every caller.</summary>
    public static IntPtr AllocCode(byte[] code)
    {
        IntPtr mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096u, MemCommit | MemReserve, PageReadWrite);
        if (mem == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(code, 0, mem, code.Length);
        uint old;
        if (VirtualProtect(mem, (UIntPtr)4096u, PageExecuteRead, &old) == 0) return IntPtr.Zero;   // leak: harmless
        FlushInstructionCache(GetCurrentProcess(), mem, (UIntPtr)(uint)code.Length);
        return mem;
    }

    /// <summary>
    /// Atomically replace the 8-byte pointer at <paramref name="slot"/> with <paramref name="value"/>, only if it
    /// still holds <paramref name="expected"/>. The slot may sit inside executable code, so the page is flipped to
    /// <c>PAGE_EXECUTE_READWRITE</c> (never dropping EXECUTE — other threads may be running code on the same page)
    /// and restored after. <c>lock cmpxchg</c> is atomic for an unaligned operand that stays inside one cache line
    /// (callers check that first), so a concurrent <c>jmp [slot]</c> reads either the old or the new pointer.
    /// </summary>
    public static bool CompareExchangePointer(IntPtr slot, IntPtr value, IntPtr expected, out string? error)
    {
        error = null;
        uint old;
        if (VirtualProtect(slot, (UIntPtr)8u, PageExecuteReadWrite, &old) == 0)
        {
            error = "VirtualProtect(RWX) on the detour slot failed";
            return false;
        }
        long seen = Interlocked.CompareExchange(ref *(long*)slot, (long)value, (long)expected);
        uint ignored;
        VirtualProtect(slot, (UIntPtr)8u, old, &ignored);
        FlushInstructionCache(GetCurrentProcess(), slot, (UIntPtr)8u);
        if (seen != (long)expected) { error = $"slot changed under us (0x{seen:X})"; return false; }
        return true;
    }
}
