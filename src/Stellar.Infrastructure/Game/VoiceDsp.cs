using System;

namespace Stellar.Infrastructure.Game;

// GVoice uplink-processing controls (promoted from StellarExperimentPlugin; spec: Voice-Chat-GVoice.md "Native
// findings"). Both exports are x64 cdecl, return a GCloudVoice errno (0 = OK, 0x1009 NEED_INIT before
// GCloudVoice_Init, 0x100A engine null):
//   int GCloudVoice_invoke(int cmd, int p1, int p2, int* result) — undocumented router → CEngine::Invoke
//   int GCloudVoice_SetBitRate(int bps)                           — CEngine::Invoke 0x138C → re-creates Opus 16k mono
//
// Resolved through GetModuleHandleW + GetProcAddress on the SAME already-loaded GCloudVoice.dll the capture hook
// patches, instead of [DllImport]: no loader probing (the DLL lives under <game>\*_Data\Plugins\x86_64, not next to
// the exe), and a missing module/export becomes a return code rather than a DllNotFound/EntryPointNotFound throw.
//
// MAIN THREAD ONLY (the framework maintainer tick). Never call these from the DirectSound Lock hook — SetBitRate
// re-creates the encoder and the invoke cmds post into the engine; neither belongs on GVoice's capture thread.
internal static unsafe class VoiceDsp
{
    public const int CmdEnableNs  = 0x21AC;   // p1 != 0 → noise suppression on
    public const int CmdEnableVad = 0x21AD;   // p1 != 0 → voice activity detection on
    public const int CmdEnableAgc = 0x21AE;   // p1 != 0 → auto gain control on
    public const int DefaultBitRate = 32000;  // AutoEnc type 4 (Opus 0x1006) default
    public const int MaxBitRate = 64000;      // usable ceiling for the GVoice Opus uplink — 96k/128k don't take (confirmed in-game 2026-10-18)

    // Plugin-side sentinels (GVoice errnos are >= 0).
    public const int RcNotCalled = int.MinValue;
    public const int RcNoModule  = -1;
    public const int RcNoExport  = -2;
    public const int RcThrew     = -3;

    private static IntPtr s_invoke, s_setBitRate;

    public static int Invoke(int cmd, int p1)
    {
        try
        {
            int rc = Resolve("GCloudVoice_invoke", ref s_invoke);
            if (rc != 0) return rc;
            int result = 0;
            return ((delegate* unmanaged[Cdecl]<int, int, int, int*, int>)s_invoke)(cmd, p1, 0, &result);
        }
        catch { return RcThrew; }
    }

    public static int SetBitRate(int bps)
    {
        try
        {
            int rc = Resolve("GCloudVoice_SetBitRate", ref s_setBitRate);
            if (rc != 0) return rc;
            return ((delegate* unmanaged[Cdecl]<int, int>)s_setBitRate)(bps);
        }
        catch { return RcThrew; }
    }

    // Cached once found; retried while missing (cheap) so a late-loaded DLL is still picked up.
    private static int Resolve(string export, ref IntPtr fn)
    {
        if (fn != IntPtr.Zero) return 0;
        var gv = VoiceNative.GetModuleHandleW("GCloudVoice.dll");
        if (gv == IntPtr.Zero) return RcNoModule;
        fn = VoiceNative.GetProcAddress(gv, export);
        return fn == IntPtr.Zero ? RcNoExport : 0;
    }

    // "(0)" / "(0x1009)" / "(no dll)" …, or "" when never called.
    public static string Rc(int rc) => rc switch
    {
        RcNotCalled => "",
        0           => "(0)",
        RcNoModule  => "(no dll)",
        RcNoExport  => "(no export)",
        RcThrew     => "(threw)",
        _           => $"(0x{rc:X})",
    };
}
