using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;
using UnityEngine.EventSystems;
namespace Stellar.Infrastructure.UI;

/// <summary>Raw input for an input-shield holder, read from <c>UnityEngine.Input</c> — upstream of the game's ignore mask,
/// exactly like the framework hotkeys (recon E). Mouse delta and wheel are sampled once per rendered frame; so is the
/// over-game-UI raycast. Main thread.</summary>
internal sealed partial class UnityShieldInputReader : IShieldInputReader
{
    /// <summary>Stellar's own canvases sort at or above this: the layout editor's input blocker (32749), the HUD (32750),
    /// toasts (32752), windows (32755), edit chrome (32758), the input blocker (32760) and click-away popups (1 000 000).
    /// Anything below is the game's.</summary>
    internal const int StellarMinSortingOrder = 32749;

    private readonly IPluginLog _log;
    private readonly Il2CppSystem.Collections.Generic.List<RaycastResult> _uiHits = new();
    private int _uiFrame = -1;
    private bool _overGameUi;

    public UnityShieldInputReader(IPluginLog log) => _log = log;

    /// <summary>True when the TOP-MOST raycast hit under the pointer is a game canvas: a Stellar window over a game window
    /// wins, so the free camera's own window check stays in charge there.</summary>
    public bool PointerOverGameUi
    {
        get
        {
            var frame = Time.frameCount;
            if (frame == _uiFrame) return _overGameUi;
            _uiFrame = frame;
            _overGameUi = RaycastGameUi();
            return _overGameUi;
        }
    }

    private bool RaycastGameUi()
    {
        try
        {
            var es = EventSystem.current;
            if (es == null) return false;
            var p = Input.mousePosition;
            var ped = new PointerEventData(es) { position = new Vector2(p.x, p.y) };
            _uiHits.Clear();
            es.RaycastAll(ped, _uiHits);
            var over = _uiHits.Count > 0 && IsGameSortingOrder(_uiHits[0].sortingOrder);
            OnGameUiProbe(over);
            return over;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsGameSortingOrder(int sortingOrder) => sortingOrder < StellarMinSortingOrder;

    partial void OnGameUiProbe(bool over);
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
