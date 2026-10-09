namespace Stellar.Application.Services;

/// <summary>
/// Pure decision for a consumer that caches <see cref="Abstractions.IThemeOverrides.Slots"/> keyed on the
/// registry's (<see cref="Abstractions.IThemeOverrides.SlotCount"/>, <see cref="Abstractions.IThemeOverrides.Revision"/>)
/// pair: a <c>Relabel</c> bumps <c>Revision</c> without ever changing <c>SlotCount</c> (a relabelled slot set
/// has exactly the same slots), so count-equality alone must never short-circuit a refresh — see
/// <c>Stellar.Infrastructure.UI.SettingsPanels.ThemeEditorBody</c>'s <c>SlotAt</c> (the consumer this was
/// extracted from, for a Unity-free unit test; that class itself cannot be built in <c>Stellar.Application.Tests</c>).
/// </summary>
internal static class SlotCacheRefresh
{
    /// <summary>True when the cached pair no longer matches the registry's current pair and the cache must
    /// be refreshed (re-read <c>Slots</c>, re-store both numbers).</summary>
    public static bool ShouldRefresh(int currentCount, int currentRevision, int cachedCount, int cachedRevision)
        => currentCount != cachedCount || currentRevision != cachedRevision;
}
