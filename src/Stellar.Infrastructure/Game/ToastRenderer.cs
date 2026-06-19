using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.Unity;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// IL2CPP host for the animated notification-toast stack. Owns a dedicated screen-space-overlay
/// <see cref="Canvas"/> (above the HUD, below the input blocker), reads the live toast set from
/// <see cref="NotificationService"/> each framework tick, diffs by <c>Id</c> to spawn / exit / reflow
/// cards, and steps every card through <see cref="ToastAnimator"/>. SRP-tight: sprite baking →
/// <see cref="ToastThemeAssets"/>, GO build → <see cref="ToastCardBuilder"/>, tween math →
/// <see cref="ToastAnimator"/>.
/// </summary>
internal sealed class ToastRenderer
{
    private const int ToastSortingOrder = 32752;   // above HUD (32750), below input blocker (32760)
    private const float TopMargin = 48f;
    private const float StackGap = 10f;
    private const int MaxVisible = 5;

    private readonly IPluginLog _log;
    private readonly IThemeHudColors _colors;
    private readonly NotificationService _service;
    private readonly ToastThemeAssets _assets = new();

    // Per-card record: the animator state + the captured lifetime (so EXIT is driven by the
    // renderer's clock, independent of Drain which drops the toast at ExpiresAt). Ordered newest→oldest.
    private sealed class Entry
    {
        public long Id;
        public double ExpiresAt;
        public ToastAnimator.Card Card = null!;
        public GameObject Root = null!;
    }

    private readonly List<Entry> _entries = new();   // index 0 = newest (top of stack)
    private GameObject? _canvas;
    private Transform? _canvasRoot;
    private ToastAnimator? _animator;
    private ToastCardBuilder? _builder;
    private bool _typeRegistered;

    public ToastRenderer(IPluginLog log, IThemeHudColors colors, NotificationService service)
    {
        _log = log; _colors = colors; _service = service;
    }

    /// <summary>Active-theme switch: rebake the text colours and drop the canvas. Active toasts are
    /// rebuilt on the next tick from the live <see cref="NotificationService"/> set.</summary>
    public void InvalidateTheme()
    {
        _assets.Rebake(_colors);
        DropCanvas();
    }

    /// <summary>Framework teardown: destroy the canvas (and its cards) + the baked assets.</summary>
    public void Shutdown()
    {
        DropCanvas();
        _assets.DestroyAll();
    }

    private void DropCanvas()
    {
        if (_canvas != null) UnityEngine.Object.Destroy(_canvas);
        _canvas = null;
        _canvasRoot = null;
        _animator = null;
        _builder = null;
        _entries.Clear();
    }

    /// <summary>Per-tick: pull the live toast set, diff by Id (spawn/exit), step every card,
    /// destroy finished cards, and reflow stack-Y. <paramref name="dt"/> is the framework tick
    /// delta (NOT Time.deltaTime). No-op-cheap when nothing is active and nothing is animating.</summary>
    public void Tick(float dt)
    {
        var now = _service.Now;
        var active = _service.Drain(now);
        if (active.Count == 0 && _entries.Count == 0) return;
        if (!EnsureCanvas()) return;

        SpawnNew(active);
        UpdateExits(active, now);
        StepAndReap(dt);
        Reflow();
    }

    // Spawn a card for any active toast we aren't already showing. Active is oldest→first; we
    // insert newest at index 0 so the stack grows downward with newest on top.
    private void SpawnNew(IReadOnlyList<ActiveToast> active)
    {
        for (var i = 0; i < active.Count; i++)
        {
            var t = active[i];
            if (HasEntry(t.Id)) continue;
            var built = _builder!.Build(_canvasRoot!, t.Message, t.Kind);
            double expiresAt = t.ExpiresAt;
            float duration = t.Duration;
            built.Group.alpha = 0f;   // ENTER fades in
            var anim = MakeCard(built, expiresAt, duration);
            _entries.Insert(0, new Entry { Id = t.Id, ExpiresAt = expiresAt, Card = anim, Root = built.Root });
        }
    }

    // Begin EXIT on cards whose lifetime is nearly up, OR that overflow the newest-5 window. The
    // overflow check exits the OLDEST live card first.
    private void UpdateExits(IReadOnlyList<ActiveToast> active, double now)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (now >= _entries[i].ExpiresAt - ToastAnimator.ExitDur)
                ToastAnimator.BeginExit(_entries[i].Card);

        int live = 0;
        for (var i = 0; i < _entries.Count; i++)
            if (_entries[i].Card.State != ToastAnimator.Phase.Exit) live++;
        // Exit oldest-first (highest index) until within the window.
        for (var i = _entries.Count - 1; i >= 0 && live > MaxVisible; i--)
        {
            if (_entries[i].Card.State == ToastAnimator.Phase.Exit) continue;
            ToastAnimator.BeginExit(_entries[i].Card);
            live--;
        }
    }

    private void StepAndReap(float dt)
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            _animator!.StepCard(e.Card, dt);
            if (e.Card.Finished)
            {
                if (e.Root != null) UnityEngine.Object.Destroy(e.Root);
                _entries.RemoveAt(i);
            }
        }
    }

    // Running-sum stack-Y: newest (index 0) sits at y=0 (the canvas anchor is already TopMargin
    // below the top); each subsequent card drops by the previous card's height + StackGap. Cards in
    // EXIT keep their slot (so the stack doesn't snap) until they finish.
    private void Reflow()
    {
        float y = 0f;
        for (var i = 0; i < _entries.Count; i++)
        {
            var card = _entries[i].Card;
            card.TargetY = -y;
            float h = card.Rect != null ? card.Rect.rect.height : 0f;
            y += h + StackGap;
        }
    }

    private bool HasEntry(long id)
    {
        for (var i = 0; i < _entries.Count; i++) if (_entries[i].Id == id) return true;
        return false;
    }

    // Wire the animator Card from a freshly-built card handle. The countdown closure reads the
    // SERVICE clock each step (same monotonic clock the toast was enqueued against) so the bar
    // drains linearly over the toast's lifetime.
    private ToastAnimator.Card MakeCard(ToastCard built, double expiresAt, float duration)
    {
        var svc = _service;
        return new ToastAnimator.Card
        {
            Group = built.Group,
            Rect = built.Rect,
            Countdown = built.Countdown,
            Countdown01 = () => duration <= 0f ? 0f : Mathf.Clamp01((float)((expiresAt - svc.Now) / duration)),
        };
    }

    /// <summary>Lazily create the toast canvas + animator. Re-creates after a scene change destroyed it.</summary>
    private bool EnsureCanvas()
    {
        if (_canvas != null) return true;
        try
        {
            var go = new GameObject("StellarToastCanvas") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = true;
            canvas.sortingOrder = ToastSortingOrder;
            if (!_typeRegistered)
            {
                try { ClassInjector.RegisterTypeInIl2Cpp<ToastAnimator>(); } catch { /* already registered */ }
                _typeRegistered = true;
            }
            _animator = go.AddComponent<ToastAnimator>();

            // Stack anchor: top-centre, TopMargin px below the top edge. Cards pivot top-centre and
            // grow downward from here (newest at y=0).
            var stack = new GameObject("Stack").AddComponent<RectTransform>();
            stack.SetParent(go.transform, worldPositionStays: false);
            stack.anchorMin = stack.anchorMax = stack.pivot = new Vector2(0.5f, 1f);
            stack.anchoredPosition = new Vector2(0f, -TopMargin);
            _canvasRoot = stack;

            _canvas = go;
            _assets.EnsureBaked(_colors);
            _builder = new ToastCardBuilder(_assets);
            _log.Info("[Toast] Stellar toast canvas created");
            return true;
        }
        catch (Exception ex) { _log.Error($"[Toast] canvas create threw: {ex.Message}"); _canvas = null; return false; }
    }
}
