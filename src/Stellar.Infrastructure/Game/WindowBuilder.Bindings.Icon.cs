using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Live tile-icon binding for WindowBuilder. Split into its own partial file so WindowBuilder.Bindings.cs
// stays under the file-size gate.
internal sealed partial class WindowBuilder
{
    // Poll-diffed live tile icon: re-pulls the icon-bytes Func and swaps RawImage.texture only when the
    // resolved byte[] reference changes. Plugins register ASYNCHRONOUSLY, after the launcher is built, and the
    // three launcher mode layouts (Full / Minimal-vertical / Minimal-horizontal) each materialise their tiles
    // at different times — so without this, a tile built before its plugin's IconPng arrived would bake the
    // Icon("plugins") fallback forever (the "icon shows in expanded but the puzzle in minimal" bug). The
    // resolver hands back STABLE references (LauncherIcons caches per name; LauncherEntry.IconPng is a fixed
    // field), so a reference compare is O(1) and never churns. Texture decode is deduped via the shared
    // byte[]-keyed cache (owned by IconTextures). A transient null keeps the last texture rather than blanking.
    internal sealed class IconBinding
    {
        public RawImage Raw = null!;
        public Func<byte[]?> Bytes = null!;
        public Func<byte[], Texture2D?> Load = null!;
        public Dictionary<byte[], Texture2D> Cache = null!;
        private byte[]? _last;
        private bool _init;
        public void Apply()
        {
            if (Raw == null || !Raw.gameObject.activeInHierarchy) return;
            var b = Bytes();
            if (_init && ReferenceEquals(b, _last)) return;   // unchanged → nothing to do
            _init = true; _last = b;
            if (b is not { Length: > 0 }) return;             // keep the last texture on a transient null
            if (!Cache.TryGetValue(b, out var tex)) { tex = Load(b); if (tex != null) Cache[b] = tex; }
            if (tex != null) Raw.texture = tex;
        }
    }
}
