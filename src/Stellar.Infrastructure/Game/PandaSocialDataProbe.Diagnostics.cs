using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="PandaSocialDataProbe"/>. Gated behind
/// <c>STELLAR_DIAGNOSTICS=1</c> so the steady-state Return path pays zero cost;
/// flip it on to confirm the first social reply decoded and fed the cache.
/// </summary>
internal sealed partial class PandaSocialDataProbe
{
    private bool _diagFirstSocialDecodeLogged;

    /// <summary>One-shot confirmation that a <c>Social.GetSocialData</c> reply decoded and fed the cache.
    /// NOTE (live-verified 2026-06-13): reply richness is MASK-dependent. Nameplate/avatar queries carry
    /// identity only (char_id + basic_data + avatar_info + personal_zone + privilege_data), so a first
    /// decode with <c>gear/fashion/fp/prof</c> = 0 is normal. The native ID card fetches mask 0 = ALL
    /// sections (profession, equip, fashion, fight point, team, union, master score) — those populate the
    /// cache whenever a card is opened. Only the live stat sheet + skills stay AOI-broadcast-only.</summary>
    private void DiagFirstSocialDecode(SocialSnapshot s)
    {
        if (!StellarDiagnostics.IsEnabled || _diagFirstSocialDecodeLogged) return;
        _diagFirstSocialDecodeLogged = true;

        _log.Info(
            $"[EntityDetail] first social decode (char={s.CharId} name={s.Name} level={s.Level} " +
            $"fp={s.FightPoint} prof={s.ProfessionId} gear={s.Gear.Count} fashion={s.Fashion.Count})");
    }

    private bool _avatarUrlOneShot;

    /// <summary>One-shot (fires regardless of the diagnostics toggle) confirmation that avatar
    /// picture URLs were parsed out of a <c>Social.GetSocialData</c> reply's <c>avatar_info</c>.</summary>
    private void LogAvatarUrlOneShot(SocialSnapshot s)
    {
        if (_avatarUrlOneShot || s.HalfBodyUrl.Length == 0) return;
        _avatarUrlOneShot = true;

        _log.Info($"[Stellar] first avatar URLs parsed: char={s.CharId} profile={s.ProfileUrl} halfBody={s.HalfBodyUrl}");
    }

    private bool _collectPointsOneShot;

    /// <summary>One-shot (fires regardless of the diagnostics toggle) confirmation that a parsed
    /// <see cref="SocialIdentity"/> carried non-zero personal-zone collection-point data — logs all
    /// three candidates once so the ID-card "collection points" badge source can be confirmed later.</summary>
    private void LogCollectPointsOneShot(SocialSnapshot s)
    {
        if (_collectPointsOneShot) return;
        var id = s.Identity;
        if (id.FashionCollect == 0 && id.RideCollect == 0 && id.WeaponSkinCollect == 0) return;
        _collectPointsOneShot = true;

        _log.Info($"[Stellar] collect points parsed: char={s.CharId} fashion={id.FashionCollect} ride={id.RideCollect} weaponSkin={id.WeaponSkinCollect}");
    }
}
