using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IEntityPortrait"/> implementation: <see cref="PandaPortraitModelProbe"/> creates the posed
/// real-outfit <c>ZModel</c> through the game's social-data pipeline (async), and <see cref="PortraitModelHost"/>
/// renders it through the game's own <c>ZModel2RT</c> render feature (the only path that isolates one model on a
/// clean background — the custom SRP renders the world globally, so a plain camera can't). Because creation
/// awaits a social-data RPC, the model is adopted lazily from the <see cref="Texture"/> poll (the overlay reads
/// it every frame while the portrait box is visible) — no extra ticker needed.
/// </summary>
internal sealed class EntityPortraitService : IEntityPortrait
{
    private readonly PandaPortraitModelProbe _probe;
    private readonly PortraitModelHost _host;
    private bool _awaitingModel;

    public EntityPortraitService(PandaPortraitModelProbe probe, PortraitModelHost host)
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

    public void Show(EntityId entity)
    {
        if (!entity.IsPlayer) { Hide(); return; }
        if (!_host.EnsureCreated()) return;
        ReleaseModel();                       // switching subjects while open: drop the old model first
        _host.ApplyTuning();
        _host.SetVisible(true);
        _probe.BuildModel(entity.Uid);
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

    public void Orbit(float dx, float dy) => _host.Orbit(dx, dy);

    public void Zoom(float delta) => _host.Zoom(delta);

    public void Pan(float dx, float dy) => _host.Pan(dx, dy);

    public void SetViewport(int width, int height) => _host.SetViewport(width, height);

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
