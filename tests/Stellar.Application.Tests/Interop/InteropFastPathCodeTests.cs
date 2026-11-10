using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stellar.Infrastructure.Unity;
using Xunit;

namespace Stellar.Application.Tests.Interop;

// Il2CppInteropFastPath: the native sign-check stubs in front of Il2CppInterop's Class_FromIl2CppType and
// MetadataCache_GetTypeInfoFromTypeDefinitionIndex managed hooks.
// The patching itself only runs in game; these pin the byte-level pieces — the stub, the token sign rule, the Dobby
// slot decoder and the trampoline jump-back check — and, on Windows x64, EXECUTE the stub against two native targets.
public sealed unsafe class InteropFastPathCodeTests
{
    [Theory]
    [InlineData(0L, true)]
    [InlineData(1L, true)]
    [InlineData(0x7FF6_1234_5678L, true)]     // a real Il2CppTypeDefinition* / metadata pointer
    [InlineData(long.MaxValue, true)]
    [InlineData(-1L, false)]
    [InlineData(-2L, false)]                   // first token InjectorHelpers.CreateClassToken mints
    [InlineData(long.MinValue, false)]
    public void Sign_rule_matches_the_managed_hook(long data, bool fast)
        => Assert.Equal(fast, InteropFastPathCode.TakesFastPath(data));

    [Fact]
    public void Stub_bytes_are_exact()
    {
        byte[] b = InteropFastPathCode.BuildStub(0x1122334455667788, unchecked((long)0x99AABBCCDDEEFF00));
        Assert.Equal(InteropFastPathCode.StubLength, b.Length);
        Assert.Equal(45, b.Length);
        Assert.Equal(new byte[]
        {
            0x48, 0x81, 0xF9, 0x00, 0x00, 0x01, 0x00,   // cmp rcx, 0x10000
            0x72, 0x16,                                 // jb  managed
            0x48, 0x8B, 0x01,                           // mov rax, [rcx]
            0x48, 0x85, 0xC0,                           // test rax, rax
            0x78, 0x0E,                                 // js  managed
            0xFF, 0x25, 0, 0, 0, 0,                     // jmp [rip] → original
        }, b[..23]);
        Assert.Equal(23, InteropFastPathCode.StubOriginalSlot);
        Assert.Equal(0x1122334455667788, BitConverter.ToInt64(b, InteropFastPathCode.StubOriginalSlot));
        Assert.Equal(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }, b[31..37]);
        Assert.Equal(37, InteropFastPathCode.StubManagedSlot);
        Assert.Equal(unchecked((long)0x99AABBCCDDEEFF00), BitConverter.ToInt64(b, InteropFastPathCode.StubManagedSlot));
        // Both early branches land exactly on the second jmp: jb 9 + 0x16 = 31, js 17 + 0x0E = 31.
        Assert.Equal(InteropFastPathCode.StubManagedJmp, 9 + b[8]);
        Assert.Equal(InteropFastPathCode.StubManagedJmp, 17 + b[16]);
        // The cmp imm32 is the null-page bound.
        Assert.Equal(InteropFastPathCode.StubMinPointer, BitConverter.ToInt32(b, 3));
    }

    // ── Dobby slot decoder over a fake address space ───────────────────────────────────────────────────────────────
    private sealed class FakeMem
    {
        private readonly Dictionary<long, byte> _m = new();
        public void Put(long at, params byte[] bytes) { for (int i = 0; i < bytes.Length; i++) _m[at + i] = bytes[i]; }
        public void PutPtr(long at, long v) => Put(at, BitConverter.GetBytes(v));
        public byte[]? Read(IntPtr a, int len)
        {
            var r = new byte[len];
            for (int i = 0; i < len; i++) if (!_m.TryGetValue((long)a + i, out r[i])) return null;
            return r;
        }
    }

    private const long T = 0x7FF6_0000_1470, M = 0x0000_7FFA_0000_2000;

    [Fact]
    public void Decoder_finds_inline_absolute_jmp_slot()
    {
        var mem = new FakeMem();
        mem.Put(T, 0xFF, 0x25, 0, 0, 0, 0);
        mem.PutPtr(T + 6, M);
        Assert.True(InteropFastPathCode.TryFindDetourSlot((IntPtr)T, (IntPtr)M, mem.Read, out IntPtr slot, out _));
        Assert.Equal((IntPtr)(T + 6), slot);
    }

    [Fact]
    public void Decoder_follows_near_branch_to_an_absolute_jmp()
    {
        var mem = new FakeMem();
        const long X = T + 0x1000;
        mem.Put(T, 0xE9); mem.Put(T + 1, BitConverter.GetBytes((int)(X - (T + 5)))); mem.Put(T + 5, 0xCC);   // decoder reads 6 bytes per hop
        mem.Put(X, 0xFF, 0x25, 0x10, 0, 0, 0);       // disp32 = 0x10 → slot at X+6+0x10
        mem.PutPtr(X + 0x16, M);
        Assert.True(InteropFastPathCode.TryFindDetourSlot((IntPtr)T, (IntPtr)M, mem.Read, out IntPtr slot, out _));
        Assert.Equal((IntPtr)(X + 0x16), slot);
    }

    [Theory]
    [InlineData(new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57 })]   // unhooked prologue
    [InlineData(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC })]
    public void Decoder_rejects_unknown_layouts(byte[] bytes)
    {
        var mem = new FakeMem();
        mem.Put(T, bytes);
        Assert.False(InteropFastPathCode.TryFindDetourSlot((IntPtr)T, (IntPtr)M, mem.Read, out _, out string why));
        Assert.Contains("unexpected bytes", why);
    }

    [Fact]
    public void Decoder_rejects_a_slot_that_does_not_lead_to_the_managed_thunk()
    {
        var mem = new FakeMem();
        mem.Put(T, 0xFF, 0x25, 0, 0, 0, 0);
        mem.PutPtr(T + 6, 0x1234_5678);                 // points into unmapped fake memory
        Assert.False(InteropFastPathCode.TryFindDetourSlot((IntPtr)T, (IntPtr)M, mem.Read, out _, out string why));
        Assert.Contains("unreadable", why);
    }

    [Fact]
    public void Decoder_rejects_rel32_straight_to_thunk()
    {
        var mem = new FakeMem();
        const long near = T + 0x100;
        mem.Put(T, 0xE9); mem.Put(T + 1, BitConverter.GetBytes((int)(near - (T + 5)))); mem.Put(T + 5, 0xCC);   // decoder reads 6 bytes per hop
        Assert.False(InteropFastPathCode.TryFindDetourSlot((IntPtr)T, (IntPtr)near, mem.Read, out _, out string why));
        Assert.Contains("no pointer slot", why);
    }

    [Fact]
    public void Jump_back_is_recognised_in_both_encodings()
    {
        // relocated 15-byte prologue, then jmp [rip+0] → T+15
        var abs = new byte[15 + 14];
        abs[15] = 0xFF; abs[16] = 0x25;
        BitConverter.GetBytes(T + 15).CopyTo(abs, 21);
        Assert.True(InteropFastPathCode.HasJumpBack(abs, (IntPtr)0x5000_0000, (IntPtr)T));

        const long tr = T - 0x2000;
        var rel = new byte[20];
        rel[10] = 0xE9;
        BitConverter.GetBytes((int)(T + 14 - (tr + 15))).CopyTo(rel, 11);
        Assert.True(InteropFastPathCode.HasJumpBack(rel, (IntPtr)tr, (IntPtr)T));

        // A jump to the function START (re-entering the hook) or far away is not a trampoline jump-back.
        var bad = new byte[14]; bad[0] = 0xFF; bad[1] = 0x25; BitConverter.GetBytes(T).CopyTo(bad, 6);
        Assert.False(InteropFastPathCode.HasJumpBack(bad, (IntPtr)0x5000_0000, (IntPtr)T));
    }

    [Theory]
    [InlineData(0x1000, true)]
    [InlineData(0x1006, true)]      // T+6 with T 16-aligned: unaligned but inside one line
    [InlineData(0x1038, true)]      // 56: last offset that still fits 8 bytes in the line
    [InlineData(0x1039, false)]     // straddles 0x1040
    public void Atomic_slot_rule(long addr, bool ok) => Assert.Equal(ok, InteropFastPathCode.IsAtomicSlot((IntPtr)addr));

    // ── Execute the real stub (Windows x64 only; CI is Linux and passes vacuously) ─────────────────────────────────
    [UnmanagedCallersOnly] private static long FastTarget(long* type, int throwOnError) => 1000 + throwOnError + (*type & 0xFF) * 10;
    [UnmanagedCallersOnly] private static long ManagedTarget(long* type, int throwOnError) => 2000 + throwOnError;

    [Fact]
    public void Stub_routes_by_sign_and_passes_arguments_through()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        long fast = (long)(delegate* unmanaged<long*, int, long>)&FastTarget;
        long managed = (long)(delegate* unmanaged<long*, int, long>)&ManagedTarget;
        IntPtr stub = ExecutableMemory.AllocCode(InteropFastPathCode.BuildStub(fast, managed));
        Assert.NotEqual(IntPtr.Zero, stub);
        var call = (delegate* unmanaged<long*, int, long>)stub;

        long* type = stackalloc long[2];
        type[0] = 0x7FF6_0000_0003; Assert.Equal(1000 + 1 + 30, call(type, 1));
        type[0] = 0;                Assert.Equal(1000 + 0, call(type, 0));
        type[0] = -2;               Assert.Equal(2000 + 1, call(type, 1));
        type[0] = long.MinValue;    Assert.Equal(2000 + 0, call(type, 0));
    }

    // Regression: GetAttr<object> → il2cpp_class_get_type(NULL) = 0x20 reached Class::FromIl2CppType. The stub must NOT
    // dereference such a pointer (a native AV failfasts the process); it routes to the managed hook, which faults
    // catchably. If the stub read [rcx] here, this test would crash the test host instead of failing.
    [Fact]
    public void Stub_routes_null_page_pointers_to_managed_without_reading_them()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        long fast = (long)(delegate* unmanaged<long*, int, long>)&FastTarget;
        long managed = (long)(delegate* unmanaged<long*, int, long>)&ManagedTarget;
        IntPtr stub = ExecutableMemory.AllocCode(InteropFastPathCode.BuildStub(fast, managed));
        Assert.NotEqual(IntPtr.Zero, stub);
        var call = (delegate* unmanaged<long*, int, long>)stub;

        Assert.Equal(2000 + 1, call((long*)0, 1));
        Assert.Equal(2000 + 0, call((long*)0x20, 0));
        Assert.Equal(2000 + 1, call((long*)(InteropFastPathCode.StubMinPointer - 1), 1));
    }

    // ── Index stub (MetadataCache::GetTypeInfoFromTypeDefinitionIndex) ─────────────────────────────────────────────
    [Theory]
    [InlineData(0, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(-1, false)]
    [InlineData(-3, false)]                    // first token InjectorHelpers.CreateClassToken mints (-2 decremented)
    [InlineData(int.MinValue, false)]
    public void Index_sign_rule_matches_the_managed_hook(int index, bool fast)
        => Assert.Equal(fast, InteropFastPathCode.TakesIndexFastPath(index));

    [Fact]
    public void Index_stub_bytes_are_exact()
    {
        byte[] b = InteropFastPathCode.BuildIndexStub(0x1122334455667788, unchecked((long)0x99AABBCCDDEEFF00));
        Assert.Equal(32, b.Length);
        Assert.Equal(InteropFastPathCode.IndexStubLength, b.Length);
        Assert.Equal(new byte[] { 0x85, 0xC9, 0x78, 0x0E, 0xFF, 0x25, 0, 0, 0, 0 }, b[..10]);   // test ecx,ecx; js; jmp [rip]
        Assert.Equal(0x1122334455667788, BitConverter.ToInt64(b, InteropFastPathCode.IndexStubOriginalSlot));
        Assert.Equal(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }, b[18..24]);
        Assert.Equal(unchecked((long)0x99AABBCCDDEEFF00), BitConverter.ToInt64(b, InteropFastPathCode.IndexStubManagedSlot));
        Assert.Equal(InteropFastPathCode.IndexStubManagedJmp, 4 + b[3]);   // js lands exactly on the second jmp
    }

    [UnmanagedCallersOnly] private static long IndexFast(int index) => 1_000_000L + index;
    [UnmanagedCallersOnly] private static long IndexManaged(int index) => -1_000_000L + index;

    [Fact]
    public void Index_stub_routes_by_sign_and_passes_the_argument_through()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        long fast = (long)(delegate* unmanaged<int, long>)&IndexFast;
        long managed = (long)(delegate* unmanaged<int, long>)&IndexManaged;
        IntPtr stub = ExecutableMemory.AllocCode(InteropFastPathCode.BuildIndexStub(fast, managed));
        Assert.NotEqual(IntPtr.Zero, stub);
        var call = (delegate* unmanaged<int, long>)stub;

        Assert.Equal(1_000_000L + 0, call(0));
        Assert.Equal(1_000_000L + 12345, call(12345));
        Assert.Equal(1_000_000L + int.MaxValue, call(int.MaxValue));
        Assert.Equal(-1_000_000L - 3, call(-3));
        Assert.Equal(-1_000_000L + int.MinValue, call(int.MinValue));
    }

    [Fact]
    public void CompareExchange_swaps_only_the_expected_value()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var page = new byte[16];
        BitConverter.GetBytes(0x1111L).CopyTo(page, 6);
        IntPtr code = ExecutableMemory.AllocCode(page);      // read+execute: the swap must flip protection itself
        IntPtr slot = code + 6;
        Assert.False(ExecutableMemory.CompareExchangePointer(slot, (IntPtr)0x2222, (IntPtr)0x9999, out _));
        Assert.Equal(0x1111L, *(long*)slot);
        Assert.True(ExecutableMemory.CompareExchangePointer(slot, (IntPtr)0x2222, (IntPtr)0x1111, out _));
        Assert.Equal(0x2222L, *(long*)slot);
        Assert.Null(ExecutableMemory.TryRead(IntPtr.Zero, 8));
        Assert.True(ExecutableMemory.IsAccessible(code, 16, mustExecute: true));
    }
}
