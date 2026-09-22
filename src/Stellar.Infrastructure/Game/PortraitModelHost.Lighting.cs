// src/Stellar.Infrastructure/Game/PortraitModelHost.Lighting.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// CUSTOM portrait lighting — the experiment half of the creature-light injector (main partial). The game's
/// <c>ECreatureRTLightType</c> presets don't differentiate our portrait: <c>SwitchLightType</c> reads a set of
/// STATIC value fields on <c>Bokura.Rendering.ZModel2RTLight</c> (not per-preset baked values), so switching the
/// preset enum pushes identical light. To actually change the look we write those static color/intensity/saturation
/// fields ourselves every frame, right before the injector's <c>SwitchLightType</c> push (the world weather VOLUME
/// re-asserts them each frame, so a one-shot write wouldn't stick — we re-apply from <see cref="InjectLight"/>).
/// <see cref="LogLightBaseline"/> is a one-shot calibration readout of the CURRENT (scene) values, captured before
/// we overwrite them. All reflection is try/caught and a missing member is skipped — the per-frame path never throws.
/// </summary>
internal sealed partial class PortraitModelHost
{
    // --- Static lighting levers on ZModel2RTLight (all STATIC) -------------------------------------------------
    // IL2CPP statics usually surface as PropertyInfo (get_/set_), not FieldInfo — resolve property-first,
    // field-fallback per member and cache the MemberInfo. A null member is skipped (logged once), not fatal.
    private Type? _lightStaticsType;
    private bool _lightStaticsTried;
    private bool _lightBaseLogged;                                    // one-shot baseline guard (reset in ClearModel)
    private readonly Dictionary<string, MemberInfo> _lightMembers = new();

    // Exact member names. ⚠️ The SKY low-light field is spelled "Skyight" (missing the l) in the game — copy verbatim.
    private const string SunColor = "creatureSunlightColor";
    private const string SunI     = "creatureSunlightColorIntensity";
    private const string SkyColor = "creatureSkylightColor";
    private const string SkyI     = "creatureSkylightColorIntensity";
    private const string PtI      = "creaturePointlightColorIntensity";
    private const string SunSat   = "creatureSunlight_ToonSaturationScale";
    private const string SkySat   = "creatureSkylight_ToonSaturationScale";
    private const string SunToonI = "creatureSunlight_ToonIntensityScale";
    private const string SkyToonI = "creatureSkylight_ToonIntensityScale";
    private const string SunLow   = "creatureSunlight_LowLightIntensity";
    private const string SkyLow   = "creatureSkyight_LowLightIntensity";   // TYPO intentional (game spelling)

    private static readonly string[] LightMemberNames =
        { SunColor, SunI, SkyColor, SkyI, PtI, SunSat, SkySat, SunToonI, SkyToonI, SunLow, SkyLow };

    // The EXACT members ApplyLightProfile overwrites — the set we snapshot/restore so the game's world creature-
    // render pass (which reads these same statics) sees untouched scene values after our cmd has captured ours.
    private static readonly string[] LightSaveMembers =
        { SunColor, SunI, SkyColor, SkyI, PtI, SunSat, SkySat, SunToonI, SkyToonI };

    private readonly Dictionary<string, object> _lightSnapshot = new();   // scene values captured before ApplyLightProfile
    private bool _lightSaved;                                             // true = _lightSnapshot holds a restorable set

    // One custom lighting profile. Index in Profiles = preset 0..4. Colors are stored as raw floats (built into an
    // interop Color only at write time) so the static initializer stays free of any interop call. First-pass
    // guesses — to be tuned against the [Portrait] lightbase: readout. LowLight fields are intentionally not written.
    private struct LightProfile
    {
        public string Name;
        public float SunR, SunG, SunB, SunI;
        public float SkyR, SkyG, SkyB, SkyI;
        public float PtI, SunSat, SkySat, SunToonI, SkyToonI;
    }

    private static readonly LightProfile[] Profiles =
    {
        new() { Name = "Bright",  SunR = 1.95f, SunG = 1.95f, SunB = 2.00f, SunI = 1.0f, SkyR = 0.50f, SkyG = 0.53f, SkyB = 0.58f, SkyI = 1.20f, PtI = 0.2f, SunSat = 1.20f, SkySat = 0.85f, SunToonI = 0.45f, SkyToonI = 0.75f },
        new() { Name = "Warm",    SunR = 2.15f, SunG = 1.90f, SunB = 1.50f, SunI = 1.0f, SkyR = 0.52f, SkyG = 0.50f, SkyB = 0.46f, SkyI = 1.15f, PtI = 0.2f, SunSat = 1.20f, SkySat = 0.85f, SunToonI = 0.45f, SkyToonI = 0.75f },
        new() { Name = "Neutral", SunR = 2.00f, SunG = 1.95f, SunB = 1.80f, SunI = 1.0f, SkyR = 0.50f, SkyG = 0.52f, SkyB = 0.55f, SkyI = 1.15f, PtI = 0.2f, SunSat = 1.15f, SkySat = 0.80f, SunToonI = 0.45f, SkyToonI = 0.75f },
        new() { Name = "Cool",    SunR = 1.55f, SunG = 1.80f, SunB = 2.10f, SunI = 1.0f, SkyR = 0.42f, SkyG = 0.50f, SkyB = 0.62f, SkyI = 1.20f, PtI = 0.2f, SunSat = 1.25f, SkySat = 0.90f, SunToonI = 0.50f, SkyToonI = 0.75f },
        new() { Name = "Vivid",   SunR = 2.00f, SunG = 1.95f, SunB = 1.95f, SunI = 1.1f, SkyR = 0.52f, SkyG = 0.55f, SkyB = 0.60f, SkyI = 1.30f, PtI = 0.3f, SunSat = 1.55f, SkySat = 1.20f, SunToonI = 0.60f, SkyToonI = 0.90f },
    };

    // Resolve + cache the type and the ~11 static members once (property-first, field-fallback). Never fatal.
    private void EnsureLightStatics()
    {
        if (_lightStaticsTried) return;
        _lightStaticsTried = true;
        try
        {
            _lightStaticsType = _types.FindType("Bokura.Rendering.ZModel2RTLight") ?? _types.FindType("ZModel2RTLight");
            if (_lightStaticsType is null) { Warn("light-statics: ZModel2RTLight not found — custom lighting disabled"); return; }
            foreach (var name in LightMemberNames)
            {
                var m = ResolveStaticMember(_lightStaticsType, name);
                if (m != null) _lightMembers[name] = m;
                else Warn($"light-statics: member '{name}' not found — skipped");
            }
            _log.Info($"[Portrait] light-statics resolved {_lightMembers.Count}/{LightMemberNames.Length}");
        }
        catch (Exception ex) { Warn($"EnsureLightStatics threw: {PortraitReflect.Unwrap(ex)}"); }
    }

    private static MemberInfo? ResolveStaticMember(Type t, string name)
    {
        const BindingFlags f = BindingFlags.Public | BindingFlags.Static;
        return (MemberInfo?)t.GetProperty(name, f) ?? t.GetField(name, f);
    }

    private object? ReadStatic(string name)
    {
        if (!_lightMembers.TryGetValue(name, out var m)) return null;
        return m is PropertyInfo p ? p.GetValue(null) : ((FieldInfo)m).GetValue(null);
    }

    private void WriteStatic(string name, object value)
    {
        if (!_lightMembers.TryGetValue(name, out var m)) return;
        if (m is PropertyInfo p) { if (p.CanWrite) p.SetValue(null, value); }
        else ((FieldInfo)m).SetValue(null, value);
    }

    private float ReadF(string name) => ReadStatic(name) is float f ? f : float.NaN;
    private string ReadC(string name) => ReadStatic(name) is Color c ? $"({c.r:0.00},{c.g:0.00},{c.b:0.00})" : "(?)";

    // ONE-SHOT calibration readout of the CURRENT (scene) static values — must fire before ApplyLightProfile
    // overwrites them, so InjectLight calls this first. Guarded by _lightBaseLogged (reset in ClearModel).
    private void LogLightBaseline()
    {
        if (_lightBaseLogged) return;
        EnsureLightStatics();
        _lightBaseLogged = true;                                     // one-shot even if a member is missing (no spam)
        if (_lightStaticsType is null) return;
        try
        {
            _log.Info($"[Portrait] lightbase: sunI={ReadF(SunI):0.00} sunC={ReadC(SunColor)} " +
                      $"skyI={ReadF(SkyI):0.00} skyC={ReadC(SkyColor)} ptI={ReadF(PtI):0.00} " +
                      $"sunSat={ReadF(SunSat):0.00} skySat={ReadF(SkySat):0.00} " +
                      $"sunToonI={ReadF(SunToonI):0.00} skyToonI={ReadF(SkyToonI):0.00} " +
                      $"sunLow={ReadF(SunLow):0.00} skyLow={ReadF(SkyLow):0.00}");
        }
        catch (Exception ex) { Warn($"LogLightBaseline threw: {PortraitReflect.Unwrap(ex)}"); }
    }

    // Write the profile's values into the static fields. Re-asserted EVERY frame from InjectLight (the volume stomps
    // them). Colors get alpha 1. LowLight fields are left untouched for now (logged only). No-op if unresolved.
    private void ApplyLightProfile(int preset)
    {
        EnsureLightStatics();
        if (_lightStaticsType is null || preset < 0 || preset >= Profiles.Length) return;
        try
        {
            var pr = Profiles[preset];
            WriteStatic(SunColor, new Color(pr.SunR, pr.SunG, pr.SunB, 1f));
            WriteStatic(SunI,     pr.SunI);
            WriteStatic(SkyColor, new Color(pr.SkyR, pr.SkyG, pr.SkyB, 1f));
            WriteStatic(SkyI,     pr.SkyI);
            WriteStatic(PtI,      pr.PtI);
            WriteStatic(SunSat,   pr.SunSat);
            WriteStatic(SkySat,   pr.SkySat);
            WriteStatic(SunToonI, pr.SunToonI);
            WriteStatic(SkyToonI, pr.SkyToonI);
        }
        catch (Exception ex) { Warn($"ApplyLightProfile threw: {PortraitReflect.Unwrap(ex)}"); }
    }

    // Snapshot the CURRENT (scene/volume) values of exactly the statics ApplyLightProfile writes, so we can put
    // them back after SwitchLightType has captured OUR values into the cmd. Must run BEFORE ApplyLightProfile.
    // No-op (leaves _lightSaved false → no restore) if the statics didn't resolve. Never throws.
    private void SaveLightStatics()
    {
        _lightSaved = false;
        if (_lightStaticsType is null) return;
        try
        {
            _lightSnapshot.Clear();
            foreach (var name in LightSaveMembers)
            {
                var v = ReadStatic(name);
                if (v != null) _lightSnapshot[name] = v;   // boxed Color/float — restored verbatim via WriteStatic
            }
            _lightSaved = _lightSnapshot.Count > 0;
        }
        catch (Exception ex) { Warn($"SaveLightStatics threw: {PortraitReflect.Unwrap(ex)}"); }
    }

    // Write the snapshot back so the game's later WORLD creature-render pass reads scene values (world unaffected).
    // Restoring AFTER SwitchLightType does NOT un-bake our values from the cmd (SetGlobal* records values at record
    // time). Guarded by _lightSaved; never throws.
    private void RestoreLightStatics()
    {
        if (!_lightSaved) return;
        try
        {
            foreach (var kv in _lightSnapshot) WriteStatic(kv.Key, kv.Value);
        }
        catch (Exception ex) { Warn($"RestoreLightStatics threw: {PortraitReflect.Unwrap(ex)}"); }
    }

    // --- TRUE isolation: EXACT world global capture/restore ---------------------------------------------------
    // The creature-light shader properties are GLOBAL GPU registers SHARED by world creatures and our preview.
    // SwitchLightType writes them and they PERSIST after our cmd executes → our tone leaks into the world. The
    // static writes above snapshot/restore the C# VALUE fields, but that can't un-set the GPU globals our cmd
    // already pushed. The old "reset to scene" re-pushed via the preview packing (SwitchLightType a 2nd time) —
    // NOT byte-identical to what the world system left there, so a residual tone leaked. Fix: CAPTURE the current
    // (world) values of these exact global registers before we overwrite, then write them back VERBATIM after the
    // portrait draw via cmd.SetGlobal*. ZModel2RTLight exposes the 5 Shader.PropertyToID HANDLES as STATIC int
    // fields — these ids ARE exactly what SwitchLightType sets. Resolve their int VALUES once (property-first).
    private const string IdSun   = "CreatureSunLightColor";     // @0x0  Vector4/Color global
    private const string IdMain1 = "CreatureMainLightParam1";   // @0x4  Vector4 global
    private const string IdCam2W = "CameraLight2World";         // @0x8  Matrix4x4 global
    private const string IdSky   = "CreatureSkyLightColor";     // @0xC  Vector4/Color global
    private const string IdMain2 = "CreatureMainLightParam2";   // @0x10 Vector4 global

    private bool _globalIdsTried;
    private bool _globalIdsOk;                                   // true = all 5 int id handles resolved
    private int _idSun, _idMain1, _idCam2W, _idSky, _idMain2;

    private bool _worldGlobalsCaptured;                         // true = the 4 vectors + matrix below hold world values
    private Vector4 _wSun, _wMain1, _wSky, _wMain2;
    private Matrix4x4 _wCam2World;

    // Resolve the 5 static int id HANDLES once (property-first, field-fallback, Public|Static — same scheme as
    // the value statics). All-or-nothing: a partial set can't safely restore, so require all 5. Never fatal.
    private void EnsureWorldGlobalIds()
    {
        if (_globalIdsTried) return;
        _globalIdsTried = true;
        EnsureLightStatics();                                    // resolves _lightStaticsType (idempotent)
        if (_lightStaticsType is null) return;
        try
        {
            if (TryReadStaticInt(_lightStaticsType, IdSun,   out _idSun)   &&
                TryReadStaticInt(_lightStaticsType, IdMain1, out _idMain1) &&
                TryReadStaticInt(_lightStaticsType, IdCam2W, out _idCam2W) &&
                TryReadStaticInt(_lightStaticsType, IdSky,   out _idSky)   &&
                TryReadStaticInt(_lightStaticsType, IdMain2, out _idMain2))
            {
                _globalIdsOk = true;
                _log.Info($"[Portrait] world-light global ids resolved (sun={_idSun} sky={_idSky} cam2w={_idCam2W})");
            }
            else Warn("world-light globals: one of the 5 static id fields missing — exact world-restore disabled");
        }
        catch (Exception ex) { Warn($"EnsureWorldGlobalIds threw: {PortraitReflect.Unwrap(ex)}"); }
    }

    private static bool TryReadStaticInt(Type t, string name, out int value)
    {
        value = 0;
        var m = ResolveStaticMember(t, name);
        var v = m is PropertyInfo p ? p.GetValue(null) : (m as FieldInfo)?.GetValue(null);
        if (v is int i) { value = i; return true; }
        return false;
    }

    // Read the CURRENT (world) values of the 5 creature-light globals BEFORE InjectLight overwrites them, so
    // RestoreWorldGlobals can write them back verbatim after the portrait draw. Reads the CPU-side global state
    // the world env manager set via Shader.SetGlobal*. ⚠️ If the world writer pushed these cmd-only and the CPU
    // cache is empty, GetGlobal* returns zero/default → the restore would write zeros; validate in-game (fallback
    // then is the render-pass-ordering approach). Never throws — safe to call from the LateUpdate path.
    private void CaptureWorldGlobals()
    {
        _worldGlobalsCaptured = false;
        EnsureWorldGlobalIds();
        if (!_globalIdsOk) return;
        try
        {
            _wSun       = Shader.GetGlobalVector(_idSun);
            _wMain1     = Shader.GetGlobalVector(_idMain1);
            _wSky       = Shader.GetGlobalVector(_idSky);
            _wMain2     = Shader.GetGlobalVector(_idMain2);
            _wCam2World = Shader.GetGlobalMatrix(_idCam2W);
            _worldGlobalsCaptured = true;
        }
        catch (Exception ex) { Warn($"CaptureWorldGlobals threw: {PortraitReflect.Unwrap(ex)}"); }
    }

    // Write the captured WORLD globals back into the cmd AFTER the portrait draw (via the _lightRestorer hook), so
    // the GPU global state ends byte-identical to what the world system left: [SwitchLightType=OUR values][Draw]
    // [SetGlobal=exact WORLD values]. No-op in Scene mode (-1), when the ids didn't resolve, or nothing captured.
    private void RestoreWorldGlobals(CommandBuffer cmd)
    {
        if (_lightPreset < 0 || !_worldGlobalsCaptured || !_globalIdsOk) return;
        try
        {
            cmd.SetGlobalVector(_idSun,   _wSun);
            cmd.SetGlobalVector(_idMain1, _wMain1);
            cmd.SetGlobalVector(_idSky,   _wSky);
            cmd.SetGlobalVector(_idMain2, _wMain2);
            cmd.SetGlobalMatrix(_idCam2W, _wCam2World);
        }
        catch (Exception ex) { Warn($"RestoreWorldGlobals threw: {PortraitReflect.Unwrap(ex)}"); }
    }
}
