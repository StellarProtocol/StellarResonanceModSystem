namespace Stellar.Infrastructure.Game;

// Which BubbleElement owns which content bindings, so a bubble re-measures its width ONLY when its own content
// changed (BubbleBinding.MarkDirty) instead of on every apply (the ChatTools-at-rest FPS fix — see BubbleBinding.Measure).
//
// Ownership is assigned by RANGE, not per build site: every binding a bubble's child subtree adds to the token's lists
// lands between the list counts taken before and after that subtree is built. So BuildBubble snapshots the counts,
// builds its child, and tags every new, still-unowned binding with itself. Innermost wins: a nested bubble tags its
// own range first (and its own BubbleBinding is then tagged by the outer one), so a change re-measures the innermost
// bubble, and a resulting width change of that bubble re-measures the next one out (BubbleBinding.Owner).
//
// A Conditional builds its branch LAZILY, later, from the window's Apply — after the range was closed. The builder's
// _bubbleOwner (the bubble whose child is being built) is therefore part of BuildScope: BuildConditional captures it,
// BuildDeferred restores it and tags the lazily built range the same way (WindowBuilder.Layout.cs). A Conditional's
// own flip already marks its bubble dirty (CondBinding.Owner), so the first measure after a lazy build is current.
//
// Content that affects a bubble's natural width only through these bindings is covered; static content never changes.
// Everything else that can move the width marks every bubble: a theme / font-scale re-skin (WindowToken.Reskin).
internal sealed partial class WindowBuilder
{
    // The bubble whose child subtree is being built right now (null = none). Saved/restored around BuildBubble's child
    // build and carried into lazy Conditional branches via BuildScope.
    private BubbleBinding? _bubbleOwner;

    // The token's binding-list counts at one moment; bindings at or past these indices were added afterwards.
    private readonly struct BindingMarks
    {
        public readonly int Texts, Styled, Buttons, Conds, Lists, Bubbles, GameTextures;
        public BindingMarks(WindowToken t)
        {
            Texts = t.Texts.Count; Styled = t.StyledTexts.Count; Buttons = t.Buttons.Count; Conds = t.Conds.Count;
            Lists = t.Lists.Count; Bubbles = t.Bubbles.Count; GameTextures = t.GameTextures.Count;
        }
    }

    // Tag every binding added since `m` that has no owner yet (a nested bubble already claimed its own) with `owner`.
    private static void TagBubbleOwner(WindowToken t, BindingMarks m, BubbleBinding owner)
    {
        for (var i = m.Texts; i < t.Texts.Count; i++) t.Texts[i].Owner ??= owner;
        for (var i = m.Styled; i < t.StyledTexts.Count; i++) t.StyledTexts[i].Owner ??= owner;
        for (var i = m.Buttons; i < t.Buttons.Count; i++) t.Buttons[i].Owner ??= owner;
        for (var i = m.Conds; i < t.Conds.Count; i++) t.Conds[i].Owner ??= owner;
        for (var i = m.Lists; i < t.Lists.Count; i++) t.Lists[i].Owner ??= owner;
        for (var i = m.GameTextures; i < t.GameTextures.Count; i++) t.GameTextures[i].Owner ??= owner;
        for (var i = m.Bubbles; i < t.Bubbles.Count; i++) if (t.Bubbles[i] != owner) t.Bubbles[i].Owner ??= owner;
    }
}
