using System;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // The Game instance, captured from a NON-per-frame lifecycle hook (Init/OnEnterScene) so the
    // resolver probe still works without a per-frame Game.Update postfix (which would reinstate the
    // ~12-18 fps managed-crossing tax). The throttled StellarTicker drives RunFrameworkTick instead.
    private object? _gameInstance;

    // Resolve the VContainer IObjectResolver off Game.GameRoot and hand it to
    // the bridges that need the container path (MessagePipe + the inventory
    // probe's container-resolved CharSerialize accessors). The first Update
    // tick fires before Game.GameRoot.Container is populated, so a one-shot
    // probe captured null and never retried. We now retry EVERY tick until a
    // non-null resolver is found, then latch.
    private void ProbeGameRootOnce(object? instance)
    {
        if (_gameRootProbed) return;

        try
        {
            var gameRoot = instance?.GetType().GetProperty("GameRoot")?.GetValue(instance);
            var resolver = Stellar.Host.ResolverProbe.FindOn(gameRoot);
            if (resolver is null)
            {
                // GameRoot.Container not ready yet — leave _gameRootProbed false
                // so the next tick retries. No log spam: this is the steady
                // pre-world state.
                return;
            }

            _gameRootProbed = true;
            _messagePipeBridge!.AttachResolver(resolver);
            _inventoryProbe!.AttachResolver(resolver);
            // Dungeon dirty-delta subscription prefers the container-resolved
            // ISubscriber<T> — attempt immediately now that the resolver is
            // attached (the framework tick keeps retrying otherwise).
            _dungeonSyncSubscription?.TrySubscribe(_messagePipeBridge!);
            Log.LogInfo($"[boot] VContainer resolver attached ({resolver.GetType().FullName})");
        }
        catch (Exception ex)
        {
            _gameRootProbed = true;
            _messagePipeBridge!.AttachResolver(null);
            Log.LogWarning($"[boot] resolver probe failed: {ex.Message}");
        }
    }
}
