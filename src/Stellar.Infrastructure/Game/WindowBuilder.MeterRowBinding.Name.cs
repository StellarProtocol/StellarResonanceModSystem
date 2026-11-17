using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

// Meter-row player name: per-script face and weight. The name slot is a bold 12 px legacy uGUI Text on the dynamic OS
// font. That font has no real bold face, so FontStyle.Bold is Unity's SYNTHETIC bold, and for Thai the glyphs come from
// whatever OS family has them ("Noto Sans Thai" is missing on stock Windows → looped Tahoma / Leelawadee). A Thai
// name ("กิมกิม") therefore drew as a smaller, smeared faux-bold of a fallback face next to crisp Latin names
// (in-game 2026-11-17, Rounded party grid). Same fix the menu text got in a7ea409:
//   - Thai → the TMP twin (ThaiTmpTwin) in the shipped StellarUIThai BOLD face, GPOS marks on: real bold, loopless,
//     matching the rest of the Stellar UI. Rounded makes it an Ellipsis twin, so the top-line fit can cut it
//     (WindowBuilder.MeterRow.RoundedFit.cs drives its slot width).
//   - Twin unavailable (sandbox / shipped face missing) and CJK / kana / Hangul (no shipped bold face) → stay on the
//     legacy Text at crisp REGULAR weight (UGuiPrimitives.EmphasisStyle — synthetic bold blurs those scripts).
//   - Latin → legacy Text, real bold, unchanged.
// Cost: runs on a name CHANGE only (one script scan); a row that never shows Thai builds nothing. Classic shares it.
internal sealed partial class WindowBuilder
{
    internal sealed partial class MeterRowBinding
    {
        private ThaiTmpTwin? _nameThai;
        private bool _nameThaiOff;   // the twin can't be built here: stop retrying on every Thai name

        private void ApplyName(string name)
        {
            if (!_nameThaiOff)
            {
                // The twin takes its weight from the legacy Text when it is first built — keep the slot's bold until then
                // (a CJK name shown earlier may have left it regular).
                if (_nameThai == null && Name.fontStyle != FontStyle.Bold) Name.fontStyle = FontStyle.Bold;
                if (ThaiTmpTwin.Route(ref _nameThai, Name, name, ellipsis: Rounded != null)) { HostNameStrike(); return; }
                if (_nameThai == null && GlyphScript.IsThai(name)) _nameThaiOff = true;
            }
            Name.text = name;
            var style = UGuiPrimitives.EmphasisStyle(emphasis: true, name);
            if (Name.fontStyle != style) Name.fontStyle = style;
            HostNameStrike();
        }

        // Per poll: mirror this poll's name colour / size onto a showing Thai twin, then the Rounded top-line fit (drop spec /
        // cut the name so the line never overlaps its right cluster). Runs at the END of Apply, after the name / spec / share
        // texts are written. It used to run from ApplyVisibility, i.e. BEFORE them: a changed spec was measured one poll
        // late, and a Thai twin would never have been measured (the fit keyed the new name as done before the twin had it).
        private void SyncNameAndFit(in MeterRowData d)
        {
            if (_nameThai is { Showing: true }) _nameThai.Sync();
            Rounded?.FitTopLine(d, _nameThai);
        }

        // The dead-state strike line is a child of the name element; the legacy Text is INACTIVE while the twin shows, so
        // move the strike onto whichever element is drawn (stretch anchors → it spans that slot).
        private void HostNameStrike()
        {
            if (NameStrikeGo == null) return;
            var host = _nameThai is { Showing: true } ? _nameThai.Go : Name.gameObject;
            if (host == null) return;
            var t = NameStrikeGo.transform;
            if (t.parent != host.transform) t.SetParent(host.transform, false);
        }
    }
}
