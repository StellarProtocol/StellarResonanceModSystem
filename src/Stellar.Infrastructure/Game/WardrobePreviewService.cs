using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IWardrobePreview"/> implementation: <see cref="PandaWardrobePreviewProbe"/> creates a self
/// <c>ZModel</c> dressed with the passed outfit (async), and its OWN <see cref="PortraitModelHost"/>
/// instance renders it into a render texture. Mirror of <see cref="EntityPortraitService"/> but with an
/// arbitrary-outfit dress; a dedicated host so it never fights the Entity-Inspector portrait's model.
/// The model is adopted lazily from the <see cref="Texture"/> poll (the overlay reads it every frame while
/// the preview pane is shown) — no extra ticker needed.
/// </summary>
internal sealed class WardrobePreviewService : IWardrobePreview
{
    private readonly PandaWardrobePreviewProbe _probe;
    private readonly PortraitModelHost _host;
    private bool _awaitingModel;

    public WardrobePreviewService(PandaWardrobePreviewProbe probe, PortraitModelHost host)
    {
        _probe = probe;
        _host = host;
    }

    public bool IsActive { get; private set; }

    public object? Texture
    {
        get
        {
            if (!IsActive) return null;
            if (_awaitingModel) TryAdoptModel();
            return _host.Texture;
        }
    }

    public void Show(EntityId self, IReadOnlyDictionary<int, int> outfit, IReadOnlyDictionary<int, float[]>? dyes = null)
    {
        if (!self.IsPlayer) { Hide(); return; }
        if (!_host.EnsureCreated()) return;
        ReleaseModel();                       // switching outfit while open: drop the old model first
        _host.ApplyTuning();
        _host.SetVisible(true);
        _probe.BuildModel(self.Uid, outfit, dyes);
        _awaitingModel = true;
        IsActive = true;
    }

    public void Hide()
    {
        if (!IsActive && !_awaitingModel) return;
        ReleaseModel();
        _host.SetVisible(false);
        IsActive = false;
    }

    public void SetViewport(int width, int height) => _host.SetViewport(width, height);

    public void Orbit(float dx, float dy) => _host.Orbit(dx, dy);

    private void ReleaseModel()
    {
        _host.ClearModel();
        _probe.ClearModel();
        _awaitingModel = false;
    }

    private void TryAdoptModel()
    {
        var model = _probe.TryTakeModel();
        if (model is null) return;
        if (_host.AssignModel(model)) _awaitingModel = false;   // false = still streaming in — retry next frame
    }
}
