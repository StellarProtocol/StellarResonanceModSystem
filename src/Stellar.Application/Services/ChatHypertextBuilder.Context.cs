using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

internal sealed partial class ChatHypertextBuilder
{
    /// <summary>
    /// The slots of one message sorted the way the game's VM sorts them before filling a template
    /// (<c>calcHyperLink</c> for shares, <c>calcNoticeParam</c> for notices). One per build, main thread.
    /// </summary>
    private sealed class HypertextContext
    {
        public HypertextContext(IReadOnlyList<ChatPlaceholder> slots, string sender, bool isShareRow)
        {
            Placeholders = slots;
            Sender = sender ?? string.Empty;
            foreach (var ph in slots) Sort(ph);
            // Position / home / blueprint / fishing / master slots exist only in shares, so they make a message a share
            // even under a row id we don't know yet; Player / Item also appear in notices, so those need the row id.
            IsShare = isShareRow || (Link is not null && Link.Type is not (ChatPlaceholderType.Player or ChatPlaceholderType.Item));
            if (!IsShare) Link = null;   // notices render their players / items as separate link runs instead
        }

        public IReadOnlyList<ChatPlaceholder> Placeholders { get; set; }
        public string Sender { get; }
        public bool IsShare { get; }

        /// <summary>The share's link slot (first link-type slot); null for a notice.</summary>
        public ChatPlaceholder? Link { get; set; }

        /// <summary>Share text before / after the link (the game keeps the LAST String on each side).</summary>
        public string String1 { get; private set; } = string.Empty;
        public string String2 { get; private set; } = string.Empty;

        public List<long> Vals { get; } = new();
        public List<ChatPlaceholder> Players { get; } = new();
        public List<ChatPlaceholder> Items { get; } = new();
        public List<ChatPlaceholder> Buffs { get; } = new();
        public List<ChatPlaceholder> Strings { get; } = new();
        public ChatPlaceholder? Union { get; private set; }
        public ChatPlaceholder? Timestamp { get; private set; }

        private void Sort(ChatPlaceholder ph)
        {
            switch (ph.Type)
            {
                case ChatPlaceholderType.String:
                    Strings.Add(ph);
                    if (Link is null) String1 = ph.Text; else String2 = ph.Text;
                    return;
                case ChatPlaceholderType.Val: Vals.Add(ph.Value); return;
                case ChatPlaceholderType.Buff: Buffs.Add(ph); return;
                case ChatPlaceholderType.Union: Union = ph; return;
                case ChatPlaceholderType.Timestamp: Timestamp = ph; return;
                case ChatPlaceholderType.Player: Players.Add(ph); break;
                case ChatPlaceholderType.Item: Items.Add(ph); break;
            }
            if (Link is null && IsLinkType(ph.Type)) Link = ph;
        }

        private static bool IsLinkType(ChatPlaceholderType t) => t is ChatPlaceholderType.Item
            or ChatPlaceholderType.Player or ChatPlaceholderType.ScenePosition or ChatPlaceholderType.Homeland
            or ChatPlaceholderType.Blueprint or ChatPlaceholderType.FishItem or ChatPlaceholderType.FishRank
            or ChatPlaceholderType.FishPersonalTotal or ChatPlaceholderType.MasterMode;
    }
}
