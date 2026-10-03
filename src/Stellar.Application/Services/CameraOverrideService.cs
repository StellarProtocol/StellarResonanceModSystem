using System;
using System.Diagnostics.CodeAnalysis;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// The camera arbiter (spec § 6): one holder; framework-ended with a reason; every pose passes the 60 m cap; an
/// exception in the holder's frame handler releases with <see cref="CameraReleaseReason.Error"/>. Main thread.
/// </summary>
internal sealed class CameraOverrideService : ICameraOverride
{
    private const float MinFov = 1f, MaxFov = 179f, MaxRoll = 90f;

    private readonly ICameraBackend _backend;
    private readonly LookAtService _lookAt;
    private readonly bool _disabled;
    private readonly Action<string> _warn;
    private Control? _holder;

    public CameraOverrideService(ICameraBackend backend, LookAtService lookAt, bool disabled, Action<string> warn)
    {
        _backend = backend;
        _lookAt = lookAt;
        _disabled = disabled;
        _warn = warn;
        _backend.Frame += OnFrame;
    }

    public bool IsOverridden => _holder is not null;
    public event Action<CameraReleaseReason>? Released;

    public bool TryAcquire([NotNullWhen(true)] out ICameraControl? control) => TryAcquire(owner: null, out control);

    internal bool TryAcquire(object? owner, [NotNullWhen(true)] out ICameraControl? control)
    {
        control = null;
        if (_disabled)
        {
            _warn("free camera is turned off on this client (STELLAR_FREECAM_OFF=1)");
            return false;
        }
        if (_holder is not null || _backend.ReadGamePose() is not CameraPose start) return false;
        if (!_backend.TryBegin(start)) return false;
        var c = new Control(this, owner, start);
        _holder = c;
        control = c;
        return true;
    }

    public IDisposable LookAtCamera() => _lookAt.Acquire(owner: null);

    public bool TryProjectToScreen(Position3D world, out ScreenPoint point)
    {
        point = default;
        if (_backend.ProjectToScreen(world) is not { } raw) return false;
        point = ScreenProjection.FromUnity(raw.X, raw.Y, raw.Depth, raw.ScreenHeight);
        return true;
    }

    internal IDisposable LookAtCamera(object? owner) => _lookAt.Acquire(owner);

    /// <summary>Framework-triggered end (zone change, cutscene, game camera mode, disconnect, shutdown).</summary>
    internal void ReleaseAll(CameraReleaseReason reason)
    {
        if (_holder is { } h) End(h, reason);
        _lookAt.ReleaseAll();
    }

    internal void ReleaseOwner(object owner)
    {
        if (_holder is { } h && Equals(h.Owner, owner)) End(h, CameraReleaseReason.PluginUnloaded);
        _lookAt.ReleaseOwner(owner);
    }

    private void SetPose(Control c, Position3D desired, float yaw, float pitch, float roll)
    {
        var pos = CameraCap.Clamp(desired, _backend.ReadLocalPlayerPosition(), c.Pose.Position);
        c.Pose = new CameraPose(pos, yaw, pitch, Math.Clamp(roll, -MaxRoll, MaxRoll), c.Pose.Fov);
        _backend.Apply(c.Pose);
    }

    private void SetFov(Control c, float fov)
    {
        c.Pose = c.Pose with { Fov = Math.Clamp(fov, MinFov, MaxFov) };
        _backend.Apply(c.Pose);
    }

    private void OnFrame(float dt)
    {
        var c = _holder;
        if (c is null) return;
        try { c.RaiseFrame(dt); }
        catch (Exception ex)
        {
            _warn($"free camera frame handler threw {ex.GetType().Name}: {ex.Message} — camera released");
            End(c, CameraReleaseReason.Error);
        }
    }

    private void End(Control c, CameraReleaseReason reason)
    {
        if (!ReferenceEquals(_holder, c)) return;
        _holder = null;
        c.Deactivate();
        try { _backend.End(); }
        catch (Exception ex) { _warn("camera hand-back threw: " + ex.Message); }
        Released?.Invoke(reason);
    }

    private sealed class Control : ICameraControl
    {
        private readonly CameraOverrideService _svc;

        public Control(CameraOverrideService svc, object? owner, CameraPose start)
        {
            _svc = svc;
            Owner = owner;
            GamePose = start;
            Pose = start;
            IsActive = true;
        }

        public object? Owner { get; }
        public CameraPose Pose { get; set; }
        public CameraPose GamePose { get; }
        public bool IsActive { get; private set; }
        public event Action<float>? Frame;

        public float Fov
        {
            get => Pose.Fov;
            set { if (IsActive) _svc.SetFov(this, value); }
        }

        public void SetPose(Position3D position, float yaw, float pitch, float roll)
        {
            if (IsActive) _svc.SetPose(this, position, yaw, pitch, roll);
        }

        public void RaiseFrame(float dt) => Frame?.Invoke(dt);

        public void Deactivate()
        {
            IsActive = false;
            Frame = null;
        }

        public void Dispose()
        {
            if (IsActive) _svc.End(this, CameraReleaseReason.Disposed);
        }
    }
}

/// <summary>Unity screen space → the plugin-facing <see cref="ScreenPoint"/>. Pure (pinned).</summary>
internal static class ScreenProjection
{
    /// <summary>Unity's <c>WorldToScreenPoint</c> result (origin bottom-left, depth along the camera's forward) as a top-left
    /// <see cref="ScreenPoint"/>; in front only at a positive, finite depth.</summary>
    public static ScreenPoint FromUnity(float x, float y, float depth, float screenHeight) =>
        new(x, screenHeight - y, depth > 0f && float.IsFinite(depth) && float.IsFinite(x) && float.IsFinite(y));
}
