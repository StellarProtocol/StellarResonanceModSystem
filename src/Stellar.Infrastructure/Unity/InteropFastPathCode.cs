using System;
using System.Text;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Pure byte-level logic behind <see cref="Il2CppInteropFastPath"/> — no IL2CPP, no process memory, so it is
/// unit-testable: the x64 early-out stub, the token sign rule it encodes, and the decoders that find Dobby's
/// detour pointer slot and confirm a trampoline jumps back into the hooked function.
/// </summary>
internal static class InteropFastPathCode
{
    /// <summary>Reads <c>len</c> bytes at an address, or null if unreadable (production: <see cref="ExecutableMemory.TryRead"/>).</summary>
    public delegate byte[]? MemReader(IntPtr addr, int len);

    /// <summary>
    /// The routing rule the stub implements. Il2CppInterop's managed hook only does anything for an
    /// <c>Il2CppType</c> whose <c>data</c> is NEGATIVE (a fake token minted by <c>InjectorHelpers.CreateClassToken</c>,
    /// counting down from -2); for everything else it calls <c>Original</c> unchanged. Real <c>data</c> values are
    /// user-mode pointers / metadata indices, so they are never negative. So: non-negative → straight to the original
    /// (fast path); negative → the managed hook, which still applies its own class/valuetype check.
    /// </summary>
    public static bool TakesFastPath(long typeData) => typeData >= 0;

    /// <summary>Total stub length in bytes.</summary>
    public const int StubLength = 36;
    /// <summary>Offset of the 8-byte absolute address of the original-function trampoline.</summary>
    public const int StubOriginalSlot = 14;
    /// <summary>Offset of the 8-byte absolute address of the managed hook's reverse-P/Invoke thunk.</summary>
    public const int StubManagedSlot = 28;

    /// <summary>
    /// x64 stub placed in front of the managed hook (Win64 ABI: <c>rcx</c> = <c>Il2CppType*</c>, <c>dl</c> = throwOnError):
    /// <code>
    ///  0: 48 8B 01             mov  rax, [rcx]          ; type->data (offset 0)
    ///  3: 48 85 C0             test rax, rax
    ///  6: 78 0E                js   managed (+14 → 22)  ; negative token → managed hook
    ///  8: FF 25 00 00 00 00    jmp  [rip+0]             ; → original trampoline
    /// 14: &lt;original abs64&gt;
    /// 22: FF 25 00 00 00 00    jmp  [rip+0]             ; → managed thunk
    /// 28: &lt;managed abs64&gt;
    /// </code>
    /// Only <c>rax</c> and flags are touched (both volatile), the stack is untouched and both exits are tail jumps,
    /// so the callee sees exactly the caller's registers and return address — the argument registers
    /// (including the full <c>edx</c>) pass through verbatim.
    /// </summary>
    public static byte[] BuildStub(long original, long managed)
    {
        var b = new byte[StubLength];
        byte[] head =
        {
            0x48, 0x8B, 0x01,
            0x48, 0x85, 0xC0,
            0x78, 0x0E,
            0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,
        };
        head.CopyTo(b, 0);
        BitConverter.GetBytes(original).CopyTo(b, StubOriginalSlot);
        new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 }.CopyTo(b, 22);
        BitConverter.GetBytes(managed).CopyTo(b, StubManagedSlot);
        return b;
    }

    /// <summary>
    /// Follow the jump chain Dobby wrote at the hooked function <paramref name="target"/> until an absolute
    /// <c>jmp [rip+disp32]</c> (<c>FF 25</c>) whose pointer slot holds <paramref name="expected"/> (the managed
    /// thunk). Accepts <c>E9 rel32</c> hops (Dobby's near-branch mode) and pointer chains, at most 4 hops.
    /// Anything else is an unknown layout and fails with the bytes in <paramref name="why"/>.
    /// </summary>
    public static bool TryFindDetourSlot(IntPtr target, IntPtr expected, MemReader read, out IntPtr slot, out string why)
    {
        slot = IntPtr.Zero;
        long at = (long)target;
        for (int hop = 0; hop < 4; hop++)
        {
            byte[]? b = read((IntPtr)at, 6);
            if (b == null) { why = $"unreadable code at 0x{at:X} (hop {hop})"; return false; }
            if (b[0] == 0xE9)
            {
                long next = at + 5 + BitConverter.ToInt32(b, 1);
                if (next == (long)expected) { why = "rel32 jmp straight to the managed thunk (no pointer slot to swap)"; return false; }
                at = next;
                continue;
            }
            if (b[0] == 0xFF && b[1] == 0x25)
            {
                long s = at + 6 + BitConverter.ToInt32(b, 2);
                byte[]? v = read((IntPtr)s, 8);
                if (v == null) { why = $"unreadable jmp slot at 0x{s:X}"; return false; }
                long val = BitConverter.ToInt64(v, 0);
                if (val == (long)expected) { slot = (IntPtr)s; why = ""; return true; }
                at = val;
                continue;
            }
            why = $"unexpected bytes [{Hex(b)}] at 0x{at:X} (hop {hop})";
            return false;
        }
        why = "jump chain longer than 4 hops";
        return false;
    }

    /// <summary>
    /// True when <paramref name="tramp"/> (bytes read from a candidate trampoline at <paramref name="trampAddr"/>)
    /// contains a jump back into the hooked function's body, i.e. to (<paramref name="target"/>,
    /// <paramref name="target"/> + <paramref name="maxPrologue"/>] — the shape every inline-hook trampoline has
    /// (relocated prologue, then continue after the overwritten bytes). Recognises <c>FF 25</c> with an in-buffer
    /// slot and <c>E9 rel32</c>.
    /// </summary>
    public static bool HasJumpBack(byte[] tramp, IntPtr trampAddr, IntPtr target, int maxPrologue = 64)
    {
        long lo = (long)target, hi = lo + maxPrologue;
        for (int i = 0; i + 5 <= tramp.Length; i++)
        {
            if (tramp[i] == 0xFF && i + 6 <= tramp.Length && tramp[i + 1] == 0x25)
            {
                int s = i + 6 + BitConverter.ToInt32(tramp, i + 2);
                if (s >= 0 && s + 8 <= tramp.Length)
                {
                    long dest = BitConverter.ToInt64(tramp, s);
                    if (dest > lo && dest <= hi) return true;
                }
            }
            if (tramp[i] == 0xE9)
            {
                long dest = (long)trampAddr + i + 5 + BitConverter.ToInt32(tramp, i + 1);
                if (dest > lo && dest <= hi) return true;
            }
        }
        return false;
    }

    /// <summary>An 8-byte write is atomic on x64 when it does not straddle a 64-byte cache line (aligned or not).</summary>
    public static bool IsAtomicSlot(IntPtr addr) => ((long)addr & 63) <= 56;

    /// <summary>Space-separated upper-case hex, for log lines.</summary>
    public static string Hex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 3);
        foreach (byte x in b) { if (sb.Length > 0) sb.Append(' '); sb.Append(x.ToString("X2")); }
        return sb.ToString();
    }
}
