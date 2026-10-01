using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using UnityEngine;
namespace Stellar.Infrastructure.UI;

/// <summary>Raw input for an input-shield holder, read from <c>UnityEngine.Input</c> — upstream of the game's ignore mask,
/// exactly like the framework hotkeys (recon E). Mouse delta and wheel are sampled once per rendered frame. Main thread.</summary>
internal sealed class UnityShieldInputReader : IShieldInputReader
{
    private int _frame = -1;
    private Vector3 _lastPointer;
    private bool _havePointer;
    private (float X, float Y) _delta;
    private float _wheel;

    public bool IsHeld(StellarKeyCode key)
    {
        try { return Input.GetKey((KeyCode)key); }
        catch { return false; }
    }

    public ModifierKeys Modifiers => UnityModifiers.Read();

    public bool IsMouseHeld(int button)
    {
        try { return Input.GetMouseButton(button); }
        catch { return false; }
    }

    public (float X, float Y) MouseDelta { get { Sample(); return _delta; } }

    public float Wheel { get { Sample(); return _wheel; } }

    public (float X, float Y) Pointer
    {
        get
        {
            try { var p = Input.mousePosition; return (p.x, Screen.height - p.y); }
            catch { return (0f, 0f); }
        }
    }

    private bool _axesMissing;

    // Raw axes keep moving while the game locks the cursor for RMB (owner report 2026-10-01). Missing axes throw once.
    private (float X, float Y, bool Ok) ReadAxes()
    {
        if (_axesMissing) return (0f, 0f, false);
        try { return (Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"), true); }
        catch { _axesMissing = true; return (0f, 0f, false); }
    }

    private void Sample()
    {
        var frame = Time.frameCount;
        if (frame == _frame) return;
        _frame = frame;
        try
        {
            _wheel = Input.mouseScrollDelta.y;
            var p = Input.mousePosition;
            var posDx = _havePointer ? p.x - _lastPointer.x : 0f;
            var posDy = _havePointer ? -(p.y - _lastPointer.y) : 0f;   // Y flipped: top-left origin
            _lastPointer = p;
            _havePointer = true;
            var (ax, ay, axisOk) = ReadAxes();
            _delta = MouseDeltaSource.Pick(axisOk, ax, ay, posDx, posDy);
        }
        catch
        {
            _wheel = 0f;
            _delta = (0f, 0f);
        }
    }
}
