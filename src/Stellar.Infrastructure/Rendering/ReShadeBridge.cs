using System;
using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// Safe managed wrapper over the Stellar ReShade bridge add-on (<c>Stellar.ReShadeBridge.addon64</c>, ABI 1). Strings
/// cross as NUL-terminated UTF-8 <c>byte[]</c>. Never throws: every call returns its default when the add-on is absent
/// or a call faults (logged once). Snapshot strings are cached per technique slot and re-decoded only when their
/// bytes change, so polling the technique list allocates nothing in the steady state. Main thread only, except
/// <see cref="RenderEventFunc"/>'s target, which Unity runs on the render thread.
/// </summary>
internal sealed partial class ReShadeBridge : IReShadeNative
{
    private const int TextBufferSize = 512;
    private const int MaxPresetPathBytes = 32 * 1024;

    private readonly IPluginLog _log;
    private readonly byte[] _nameScratch = new byte[TextBufferSize];
    private readonly byte[] _effectScratch = new byte[TextBufferSize];
    private readonly List<(CachedText Name, CachedText Effect)> _slots = new();
    private readonly CachedText _preset = new();
    private byte[] _presetBuffer = new byte[TextBufferSize];
    private bool _faultLogged;

    internal ReShadeBridge(IPluginLog log) => _log = log;

    public bool IsLoaded => TryBind();

    private Exports? Bound => TryBind() ? _exports : null;

    public ReShadeNativeStatus ReadStatus()
    {
        if (Bound is not { } x) return default;
        try
        {
            return new ReShadeNativeStatus(x.Ready() != 0, x.IsLoading() != 0, x.SnapshotFrames(), x.TechniqueCount(), x.GetEnabled() != 0);
        }
        catch (Exception ex)
        {
            Fault(ex);
            return default;
        }
    }

    public bool TryGetTechnique(int index, out string name, out string effectFile, out bool enabled)
    {
        name = effectFile = "";
        enabled = false;
        if (index < 0 || Bound is not { } x) return false;
        try
        {
            if (x.TechniqueAt(index, _nameScratch, TextBufferSize, _effectScratch, TextBufferSize, out var on) == 0) return false;
            while (_slots.Count <= index) _slots.Add((new CachedText(), new CachedText()));
            var slot = _slots[index];
            name = slot.Name.Update(_nameScratch);
            effectFile = slot.Effect.Update(_effectScratch);
            enabled = on != 0;
            return true;
        }
        catch (Exception ex)
        {
            Fault(ex);
            return false;
        }
    }

    public string? GetPreset()
    {
        if (Bound is not { } x) return null;
        try
        {
            var length = x.GetPreset(_presetBuffer, _presetBuffer.Length);
            if (length >= _presetBuffer.Length && length < MaxPresetPathBytes)
            {
                _presetBuffer = new byte[length + 1];
                length = x.GetPreset(_presetBuffer, _presetBuffer.Length);
            }
            var path = length > 0 ? _preset.Update(_presetBuffer) : "";
            return path.Length == 0 ? null : path;
        }
        catch (Exception ex)
        {
            Fault(ex);
            return null;
        }
    }

    public void RequestEnabled(bool on) => Call(x => x.RequestEnabled(on ? 1 : 0));

    public void RequestTechnique(string? effectFile, string name, bool on, bool save)
    {
        if (EncodeZ(name) is not { } nameBytes) return;
        var effectBytes = EncodeZ(effectFile);
        Call(x => x.RequestTechnique(effectBytes, nameBytes, on ? 1 : 0, save ? 1 : 0));
    }

    public void RequestPreset(string path)
    {
        if (EncodeZ(path) is not { } bytes) return;
        Call(x => x.RequestPreset(bytes));
    }

    public void RequestSearchPaths(string? effects, string? textures)
    {
        var effectBytes = EncodeZ(effects);
        var textureBytes = EncodeZ(textures);
        if (effectBytes is null && textureBytes is null) return;
        Call(x => x.RequestSearchPaths(effectBytes, textureBytes));
    }

    /// <summary>Queues an <c>ID3D11Texture2D*</c> (RGBA8, render-target capable) for the next render event; the add-on
    /// holds its own COM reference. Resets <see cref="LastRender"/> to 0.</summary>
    internal void QueueRender(IntPtr d3d11Texture, uint width, uint height) => Call(x => x.QueueRender(d3d11Texture, width, height));

    /// <summary>Result of the last render event: -1 no ReShade runtime (also: add-on absent), -2 nothing queued, -3 view
    /// creation failed, else the number of techniques drawn (0 = nothing drawn yet).</summary>
    internal int LastRender()
    {
        if (Bound is not { } x) return -1;
        try
        {
            return x.LastRender();
        }
        catch (Exception ex)
        {
            Fault(ex);
            return -1;
        }
    }

    /// <summary>The render-event callback for <c>GL.IssuePluginEvent</c>, or zero when the add-on is absent.</summary>
    internal IntPtr RenderEventFunc()
    {
        if (Bound is not { } x) return IntPtr.Zero;
        try
        {
            return x.RenderEventFunc();
        }
        catch (Exception ex)
        {
            Fault(ex);
            return IntPtr.Zero;
        }
    }

    internal static string DecodeZ(byte[] buffer) => Encoding.UTF8.GetString(buffer, 0, NulLength(buffer));

    /// <summary>NUL-terminated UTF-8, or null for a null/empty string (the add-on reads null as "none").</summary>
    internal static byte[]? EncodeZ(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    private static int NulLength(byte[] buffer)
    {
        var nul = Array.IndexOf(buffer, (byte)0);
        return nul < 0 ? buffer.Length : nul;
    }

    private void Call(Action<Exports> call)
    {
        if (Bound is not { } x) return;
        try
        {
            call(x);
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    private void Fault(Exception ex)
    {
        if (_faultLogged) return;
        _faultLogged = true;
        _log.Warning($"[ReShade] bridge call failed (further failures not logged): {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>A decoded snapshot string that is re-decoded only when its bytes change.</summary>
    private sealed class CachedText
    {
        private byte[] _bytes = Array.Empty<byte>();
        private string _text = "";

        internal string Update(byte[] buffer)
        {
            var length = NulLength(buffer);
            var current = buffer.AsSpan(0, length);
            if (current.SequenceEqual(_bytes)) return _text;
            _bytes = current.ToArray();
            _text = Encoding.UTF8.GetString(current);
            return _text;
        }
    }
}
