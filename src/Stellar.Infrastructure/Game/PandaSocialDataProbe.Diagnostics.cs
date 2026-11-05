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

    // Last-logged location per charId. Nameplate/avatar queries re-fetch the same player constantly, so
    // the location probe only logs when a player's location actually changed (incl. present↔absent).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, SocialLocation?> _lastLoggedLocation = new();

    /// <summary>Location probe (fires regardless of the diagnostics toggle): logs a player's
    /// <c>SocialData.scene_data</c> whenever it differs from the last one logged for that char, so we can
    /// learn in-game whether the server fills it for OTHER players or blanks it for privacy
    /// (<c>present=False</c> = section absent; <c>present=True</c> with all zeros = sent but blanked).</summary>
    private void LogLocationProbe(SocialSnapshot s)
    {
        var loc = s.Location;
        if (_lastLoggedLocation.TryGetValue(s.CharId, out var prev) && prev == loc) return;
        _lastLoggedLocation[s.CharId] = loc;

        var l = loc.GetValueOrDefault();
        _log.Info(
            $"[SocialLocation] char={s.CharId} name={s.Name} present={loc is not null} map={l.MapId} " +
            $"layer={l.SceneLayer} line={l.LineId} pos=({l.Pos.X:F1},{l.Pos.Y:F1},{l.Pos.Z:F1}) dir={l.Dir:F1} " +
            $"area={l.SceneAreaId} levelMap={l.LevelMapId} levelPos=({l.LevelPos.X:F1},{l.LevelPos.Y:F1},{l.LevelPos.Z:F1}) " +
            $"levelArea={l.LevelAreaId}");
    }

    /// <summary>Party probe (<c>STELLAR_DIAGNOSTICS=1</c> only): one line per reply that carries
    /// <c>SocialData.team_data</c>. It answered its question (2026-11-04: the server DOES send the full roster for
    /// players outside our party — Social-Location-Probe.md), so it no longer runs in a normal session; keep it for
    /// re-checking the decode. Not deduped like the location probe — team_data is full-mask-only (ID-card opens and
    /// EntityInspector refreshes), so it's low-volume. A present-but-zeroed section logs <c>empty</c>.</summary>
    private void LogTeamProbe(SocialSnapshot s)
    {
        if (!StellarDiagnostics.IsEnabled || s.Party is not { } t) return;
        bool empty = t.TeamId == 0 && t.LeaderId == 0 && t.TeamNum == 0 && t.CharIds.Count == 0 && t.Members.Count == 0;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[TeamDiag] char={s.CharId} name={s.Name} team={t.TeamId} leader={t.LeaderId} target={t.TeamTargetId} " +
                  $"num={t.TeamNum} type={t.MemberType} charIds={t.CharIds.Count} members={t.Members.Count}");
        if (empty) sb.Append(" empty");
        if (t.Members.Count > 0)
        {
            sb.Append(" [");
            int n = System.Math.Min(t.Members.Count, 20);   // raid parties cap at 20 — the bound is just defensive
            for (int i = 0; i < n; i++)
            {
                var m = t.Members[i];
                if (i > 0) sb.Append(' ');
                sb.Append($"{m.Name}/{m.Level}/{m.ProfessionId}/{m.OnlineStatus}/{m.SceneId}");
            }
            if (t.Members.Count > n) sb.Append(" …");
            sb.Append(']');
        }
        _log.Info(sb.ToString());
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
