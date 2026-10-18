using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Stellar.Infrastructure.Game;

// Voice injection — native DirectSound capture hook on GCloudVoice.dll (promoted from StellarExperimentPlugin,
// in-game validated 2026-10-05; spec: Voice-Chat-GVoice.md "Native findings" + "Capture backend"). GVoice's capture
// is 100% native (CAudCapDs over DirectSound by default), so Harmony can't reach the PCM. Chain:
//   1. IAT slot GCloudVoice+0x6190D8 (DSOUND ordinal 6 DirectSoundCaptureCreate — imported BY ORDINAL, so a
//      name-based IAT walker misses it) → DscCreateHook.
//   2. DscCreateHook: call real create, then patch IDirectSoundCapture vtable slot +0x18 CreateCaptureBuffer.
//   3. CreateBufferHook: call real, remember the buffer + copy its WAVEFORMATEX, patch capture-buffer Lock (+0x40).
//   4. LockHook (VoiceCaptureHook.Mix.cs): after the real Lock returns, additively mix the mixer's PCM into
//      p1/n1,p2/n2 BEFORE CAudCapDs::FillBuffer memcpys it out (Unlock entry would be too late).
// The device + buffer are created ONCE in GVoice Init (not per OpenMic), so the IAT hook must be in place before the
// game's GCloudVoice_Init. In the framework, Install() runs at boot from BootstrapPlugin.ApplyLifecyclePatches
// (title screen, before login) and force-loads the DLL. On a hot-reload (GVoice already initialised) ProbeInstall()
// finds dsound's SHARED capture-buffer vtable through a throwaway buffer of our own.
//
// The dsound vtables are shared by every object of the class, so the vtable patches are process-wide; identity
// checks (s_gvoiceCapture / s_gvoiceBuf) keep us to GVoice's objects unless probe mode is on.
internal static unsafe partial class VoiceCaptureHook
{
    private const int  IatRva            = 0x6190D8; // GCloudVoice.dll 4.3.0.253999397 — DSOUND #6
    private const int  SlotCreateBuffer  = 3;        // IDirectSoundCapture::CreateCaptureBuffer        (+0x18)
    private const int  SlotGetFormat     = 5;        // IDirectSoundCaptureBuffer::GetFormat            (+0x28)
    private const int  SlotLock          = 8;        // IDirectSoundCaptureBuffer::Lock                 (+0x40)

    private static readonly object s_patchGate = new();

    // Real function pointers (call-through targets) and the slot addresses we overwrote (restore targets).
    private static IntPtr s_realDscCreate;                   // dsound!DirectSoundCaptureCreate
    private static IntPtr s_iatSlot;
    private static IntPtr s_createBufferSlot, s_origCreateBuffer;
    private static IntPtr s_lockSlot, s_origLock;

    // GVoice's own objects — written from GVoice's init thread, compared on the capture thread.
    private static IntPtr s_gvoiceCapture, s_gvoiceBuf;

    // Diagnostics (service reads; native hooks write via Interlocked/volatile).
    public static volatile bool IatPatched, CreateBufferPatched, LockPatched, ProbeMode;
    private static int s_dscCreateCalls, s_createBufferSeen, s_vtableMismatch;

    // True when GCloudVoice.dll was ALREADY loaded at Install (hot-reload / late attach → GVoice likely already
    // created its capture buffer, so the IAT hook will never see it). The maintainer uses this to decide whether to
    // fall back to ProbeInstall — it is never set on the normal boot path (where Install force-loads the DLL first).
    public static bool InstalledWhileGVoiceLoaded { get; private set; }

    // Capture format. Written by CreateBufferHook (desc) or LockHook (GetFormat, probe mode); version bumped LAST so a
    // reader that sees a new version sees the matching fields.
    private static int s_fmtTag, s_fmtRate, s_fmtCh, s_fmtBits, s_fmtVersion;
    public static int  FormatVersion   => Volatile.Read(ref s_fmtVersion);
    public static int  FormatRate      => Volatile.Read(ref s_fmtRate);
    public static int  FormatChannels  => Volatile.Read(ref s_fmtCh);
    public static int  FormatBits      => Volatile.Read(ref s_fmtBits);
    public static bool FormatSupported => FormatVersion > 0 && s_fmtTag == 1 && s_fmtBits == 16 && s_fmtRate > 0 && s_fmtCh > 0;
    public static int  DscCreateCalls  => Volatile.Read(ref s_dscCreateCalls);
    public static int  CreateBufferSeen => Volatile.Read(ref s_createBufferSeen);

    // ── Install (boot, main thread) ──────────────────────────────────────────────────────────────────────────────
    public static string Install()
    {
        lock (s_patchGate)
        {
            if (IatPatched) return "IAT already patched";
            s_realDscCreate = ResolveRealDscCreate();
            if (s_realDscCreate == IntPtr.Zero) return "dsound!DirectSoundCaptureCreate not found";

            var gv = VoiceNative.GetModuleHandleW("GCloudVoice.dll");
            InstalledWhileGVoiceLoaded = gv != IntPtr.Zero;
            string how = "already loaded (GVoice may be initialised already — probe install will run if no buffer is seen)";
            if (gv == IntPtr.Zero)
            {
                var path = FindGVoicePath();
                if (path == null) return "GCloudVoice.dll not found under <game>\\*_Data\\Plugins";
                gv = VoiceNative.LoadLibraryW(path);
                if (gv == IntPtr.Zero) return "LoadLibrary failed: " + path;
                how = "force-loaded early";
            }

            // Bounds-check the RVA against SizeOfImage before touching it: a different DLL build could be smaller.
            int ntOff = *(int*)(gv + 0x3C);
            uint sizeOfImage = *(uint*)(gv + ntOff + 0x50); // NT sig(4)+FileHeader(0x14)+OptionalHeader.SizeOfImage(0x38)
            if ((uint)IatRva + 8 > sizeOfImage) return $"IAT RVA outside image (SizeOfImage=0x{sizeOfImage:X}) — DLL version changed";

            var slot = gv + IatRva;
            var cur  = *(IntPtr*)slot;
            // Guard: only patch if the slot really is DirectSoundCaptureCreate. Anything else ⇒ different DLL build
            // (or another hook got there first) — writing blind would redirect an unrelated import.
            if (cur != s_realDscCreate)
                return $"IAT slot mismatch (0x{cur.ToInt64():X} != DirectSoundCaptureCreate 0x{s_realDscCreate.ToInt64():X}) — DLL version changed, NOT patched";

            delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int> hook = &DscCreateHook;
            if (!VoiceNative.WritePointer(slot, (IntPtr)hook)) return "VirtualProtect failed on IAT slot";
            s_iatSlot  = slot;
            IatPatched = true;
            return "IAT patched (" + how + ")";
        }
    }

    private static IntPtr ResolveRealDscCreate()
    {
        var ds = VoiceNative.GetModuleHandleW("dsound.dll");
        if (ds == IntPtr.Zero) ds = VoiceNative.LoadLibraryW("dsound.dll");
        return ds == IntPtr.Zero ? IntPtr.Zero : VoiceNative.GetProcAddress(ds, "DirectSoundCaptureCreate");
    }

    // <game>\<Name>_Data\Plugins\x86_64\GCloudVoice.dll — derived from the running exe (StarSEA_Data on SEA; other
    // regions name the data folder differently), never a hardcoded drive.
    private static string? FindGVoicePath()
    {
        var exe = Environment.ProcessPath;
        var dir = exe == null ? null : Path.GetDirectoryName(exe);
        if (dir == null) return null;
        foreach (var data in Directory.GetDirectories(dir, "*_Data"))
        {
            var p = Path.Combine(data, "Plugins", "x86_64", "GCloudVoice.dll");
            if (File.Exists(p)) return p;
            p = Path.Combine(data, "Plugins", "GCloudVoice.dll");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ── Hooks ────────────────────────────────────────────────────────────────────────────────────────────────────
    // No exception may escape an UnmanagedCallersOnly method (it would tear the process down), and the real call's
    // HRESULT is always returned unchanged.
    [UnmanagedCallersOnly]
    private static int DscCreateHook(IntPtr guid, IntPtr* ppDsc, IntPtr outer)
    {
        int hr = ((delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int>)s_realDscCreate)(guid, ppDsc, outer);
        try
        {
            Interlocked.Increment(ref s_dscCreateCalls);
            if (hr >= 0 && ppDsc != null && *ppDsc != IntPtr.Zero)
            {
                s_gvoiceCapture = *ppDsc;
                PatchCreateBuffer(*ppDsc);
            }
        }
        catch { }
        return hr;
    }

    [UnmanagedCallersOnly]
    private static int CreateBufferHook(IntPtr self, DscBufferDesc* desc, IntPtr* ppBuf, IntPtr outer)
    {
        int hr = ((delegate* unmanaged<IntPtr, DscBufferDesc*, IntPtr*, IntPtr, int>)s_origCreateBuffer)(self, desc, ppBuf, outer);
        try
        {
            if (hr >= 0 && self == s_gvoiceCapture && ppBuf != null && *ppBuf != IntPtr.Zero)
            {
                s_gvoiceBuf = *ppBuf;
                if (desc != null && desc->Format != null) PublishFormat(desc->Format);
                Interlocked.Increment(ref s_createBufferSeen);
                PatchLock(*ppBuf);
            }
        }
        catch { }
        return hr;
    }

    // WAVE_FORMAT_EXTENSIBLE (0xFFFE) is folded to its SubFormat tag (first ushort of the GUID at +24) so a 16-bit
    // PCM extensible format still counts as PCM. Tag 1 = PCM; anything else is "unsupported" → pass-through.
    private static void PublishFormat(WaveFormatEx* f)
    {
        int tag = f->FormatTag;
        if (tag == 0xFFFE && f->CbSize >= 22) tag = *(ushort*)((byte*)f + 24);
        Volatile.Write(ref s_fmtTag,  tag);
        Volatile.Write(ref s_fmtRate, (int)f->SamplesPerSec);
        Volatile.Write(ref s_fmtCh,   f->Channels);
        Volatile.Write(ref s_fmtBits, f->BitsPerSample);
        Interlocked.Increment(ref s_fmtVersion);
    }

    // ── Vtable patching (idempotent; the vtables are dsound-global, so the first patch covers every object) ───────
    private static void PatchCreateBuffer(IntPtr dsc)
    {
        lock (s_patchGate)
        {
            var slot = (IntPtr)(*(IntPtr**)dsc + SlotCreateBuffer);
            delegate* unmanaged<IntPtr, DscBufferDesc*, IntPtr*, IntPtr, int> hook = &CreateBufferHook;
            if (*(IntPtr*)slot == (IntPtr)hook) return;
            // A second, DIFFERENT vtable would need its own saved original — don't clobber the first one's.
            if (s_createBufferSlot != IntPtr.Zero && slot != s_createBufferSlot) { Interlocked.Increment(ref s_vtableMismatch); return; }
            s_origCreateBuffer = *(IntPtr*)slot;   // set BEFORE the slot points at us — the hook calls through it
            if (VoiceNative.WritePointer(slot, (IntPtr)hook))
            {
                s_createBufferSlot  = slot;
                CreateBufferPatched = true;
            }
        }
    }

    private static void PatchLock(IntPtr buf)
    {
        lock (s_patchGate)
        {
            var slot = (IntPtr)(*(IntPtr**)buf + SlotLock);
            delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, IntPtr*, uint*, uint, int> hook = &LockHook;
            if (*(IntPtr*)slot == (IntPtr)hook) return;
            if (s_lockSlot != IntPtr.Zero && slot != s_lockSlot) { Interlocked.Increment(ref s_vtableMismatch); return; }
            s_origLock = *(IntPtr*)slot;
            if (VoiceNative.WritePointer(slot, (IntPtr)hook))
            {
                s_lockSlot  = slot;
                LockPatched = true;
            }
        }
    }

    // ── Late install: probe buffer ───────────────────────────────────────────────────────────────────────────────
    // GVoice already created its buffer before our IAT hook ⇒ we never see it. Create our own tiny capture buffer
    // (8 kHz mono 16-bit, 0.5 s) only to reach dsound's shared capture-buffer vtable, patch Lock, release. This
    // briefly OPENS THE DEFAULT MIC. Probe mode can't know GVoice's buffer pointer, so LockHook then accepts any
    // capture buffer (flags==0, bytes>0) and learns its format via GetFormat once per new buffer.
    public static string ProbeInstall()
    {
        ProbeMode = true;   // before patching, so the first hooked Lock already takes the any-buffer path
        if (LockPatched) return "Lock already patched — probe mode on (any capture buffer accepted)";
        if (s_realDscCreate == IntPtr.Zero) s_realDscCreate = ResolveRealDscCreate();
        if (s_realDscCreate == IntPtr.Zero) return "dsound!DirectSoundCaptureCreate not found";

        IntPtr dsc = IntPtr.Zero, buf = IntPtr.Zero;
        try
        {
            int hr = ((delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int>)s_realDscCreate)(IntPtr.Zero, &dsc, IntPtr.Zero);
            if (hr < 0 || dsc == IntPtr.Zero) return $"probe DirectSoundCaptureCreate failed hr=0x{hr:X8} (no capture device?)";

            var wfx  = new WaveFormatEx { FormatTag = 1, Channels = 1, SamplesPerSec = 8000, AvgBytesPerSec = 16000, BlockAlign = 2, BitsPerSample = 16 };
            var desc = new DscBufferDesc { Size = 0x28, BufferBytes = 8000, Format = &wfx };
            // If our CreateCaptureBuffer hook already sits in the slot, call the saved original directly.
            var create = VoiceNative.Slot(dsc, SlotCreateBuffer);
            delegate* unmanaged<IntPtr, DscBufferDesc*, IntPtr*, IntPtr, int> cbHook = &CreateBufferHook;
            if (create == (IntPtr)cbHook) create = s_origCreateBuffer;
            hr = ((delegate* unmanaged<IntPtr, DscBufferDesc*, IntPtr*, IntPtr, int>)create)(dsc, &desc, &buf, IntPtr.Zero);
            if (hr < 0 || buf == IntPtr.Zero) return $"probe CreateCaptureBuffer failed hr=0x{hr:X8}";

            PatchLock(buf);
            return LockPatched ? "probe: Lock patched (probe mode — mic opened briefly)" : "probe: Lock patch FAILED";
        }
        finally
        {
            VoiceNative.Release(buf);
            VoiceNative.Release(dsc);
        }
    }

    // ── Restore (framework teardown) ─────────────────────────────────────────────────────────────────────────────
    // Only rewrite a slot that still holds OUR pointer (someone may have chained over us since). The managed hook
    // code stays loaded (assemblies aren't unloaded), so a capture thread mid-call in a hook finishes safely.
    public static void Restore()
    {
        VoiceMixer.StopAll();
        lock (s_patchGate)
        {
            delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int> h1 = &DscCreateHook;
            delegate* unmanaged<IntPtr, DscBufferDesc*, IntPtr*, IntPtr, int> h2 = &CreateBufferHook;
            delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, IntPtr*, uint*, uint, int> h3 = &LockHook;
            RestoreSlot(ref s_iatSlot, s_realDscCreate, (IntPtr)h1);
            RestoreSlot(ref s_createBufferSlot, s_origCreateBuffer, (IntPtr)h2);
            RestoreSlot(ref s_lockSlot, s_origLock, (IntPtr)h3);
            IatPatched = CreateBufferPatched = LockPatched = ProbeMode = false;
        }
    }

    private static void RestoreSlot(ref IntPtr slot, IntPtr orig, IntPtr hook)
    {
        if (slot == IntPtr.Zero) return;
        if (*(IntPtr*)slot == hook) VoiceNative.WritePointer(slot, orig);
        slot = IntPtr.Zero;
    }

    public static string HookLine =>
        $"IAT {(IatPatched ? "patched" : "no")} · DSCCreate×{DscCreateCalls} · CreateBuffer " +
        $"{(CreateBufferPatched ? "patched" : "no")}/seen×{CreateBufferSeen} · Lock {(LockPatched ? "patched" : "no")}" +
        (ProbeMode ? " · PROBE MODE" : "") +
        (Volatile.Read(ref s_vtableMismatch) > 0 ? $" · vtable mismatch×{s_vtableMismatch}" : "");

    public static string FormatLine
    {
        get
        {
            if (FormatVersion == 0) return "Capture format: unknown (no buffer seen yet)";
            var s = $"Capture format: {FormatRate} Hz · {FormatChannels} ch · {FormatBits}-bit (tag {Volatile.Read(ref s_fmtTag)})";
            return FormatSupported ? s : s + " — UNSUPPORTED (16-bit PCM only), passing through";
        }
    }
}
