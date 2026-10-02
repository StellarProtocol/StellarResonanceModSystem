using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game.Posing;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// Regression pin clone-nre-male-null-ridetpl — NEVER weaken or delete. Origin: probe run 6 (2026-10-02, TEST prefix,
// /tmp/stellar-scenario-freecam-probe-1790912237.log; devkit .superpowers/sdd/posing/clone-nre-rootcause.md).
// CloneModelForPhoto threw a NullReferenceException 8/8 on idle Male players with a null AnimRideTemplate: the game's
// clone callback copies the template through SetAttrAnimRideTemplate, which does addr.Replace(...) for ModelGender == EM
// without a null check — and the copy it had already registered (modelDict_ +1) leaked, never loaded.
public sealed class CloneNreMaleNullRideTplTests
{
    private const int EM = 1, EF = 2, Default = 0, Action = 8, Interaction = 28;

    // ── The guard predicate ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void clone_nre_male_null_ridetpl_idle_male_with_null_template_is_normalised() =>
        Assert.True(CloneGuard.NeedsRideTemplateNormalise(EM, Default, 0, templateIsNull: true));

    [Fact]
    public void clone_nre_male_null_ridetpl_female_is_never_touched()
    {
        Assert.False(CloneGuard.NeedsRideTemplateNormalise(EF, Default, 0, templateIsNull: true));
        Assert.False(CloneGuard.NeedsRideTemplateNormalise(0, Default, 0, templateIsNull: true));   // Unknown
    }

    [Fact]
    public void clone_nre_male_null_ridetpl_acting_male_skips_the_template_copy() =>
        Assert.False(CloneGuard.NeedsRideTemplateNormalise(EM, Action, 9020, templateIsNull: true));

    [Fact]
    public void clone_nre_male_null_ridetpl_male_with_empty_or_real_template_is_left_alone() =>
        // "" (a state the game itself produces) and a real ride template both arrive as non-null.
        Assert.False(CloneGuard.NeedsRideTemplateNormalise(EM, Default, 0, templateIsNull: false));

    [Fact]
    public void clone_nre_male_null_ridetpl_action_state_without_an_action_id_still_copies_the_template()
    {
        Assert.True(CloneGuard.NeedsRideTemplateNormalise(EM, Action, 0, templateIsNull: true));
        Assert.True(CloneGuard.NeedsRideTemplateNormalise(EM, Interaction, 9020, templateIsNull: true));
    }

    // ── The orphan safety net ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void clone_nre_male_null_ridetpl_a_throw_after_the_game_made_a_copy_recycles_it_once_and_surfaces()
    {
        var net = new CloneOrphanNet();
        var orphan = new object();
        var recycled = new List<object>();
        var warned = new List<string>();
        var boom = new InvalidOperationException("NRE inside the clone callback");

        var thrown = Assert.Throws<InvalidOperationException>(() => net.Run(() =>
        {
            net.Record(orphan);   // the cloneModel postfix fires before the callback throws
            throw boom;
        }, recycled.Add, warned.Add));

        Assert.Same(boom, thrown);
        Assert.Equal(new[] { orphan }, recycled);
        Assert.Single(warned);
        Assert.False(net.Armed);
    }

    [Fact]
    public void clone_nre_male_null_ridetpl_a_throw_with_nothing_recorded_recycles_nothing()
    {
        var net = new CloneOrphanNet();
        var recycled = new List<object>();
        Assert.Throws<InvalidOperationException>(() =>
            net.Run(() => throw new InvalidOperationException(), recycled.Add, _ => { }));
        Assert.Empty(recycled);
        Assert.False(net.Armed);
    }

    [Fact]
    public void clone_nre_male_null_ridetpl_a_success_disarms_and_recycles_nothing()
    {
        var net = new CloneOrphanNet();
        var copy = new object();
        var recycled = new List<object>();
        var result = net.Run(() =>
        {
            net.Record(copy);
            return copy;
        }, recycled.Add, _ => { });

        Assert.Same(copy, result);
        Assert.False(net.Armed);
        Assert.Empty(recycled);
        net.Record(new object());   // an unarmed postfix records nothing …
        Assert.Throws<InvalidOperationException>(() =>   // … so a later failure has nothing stale to remove
            net.Run(() => throw new InvalidOperationException(), recycled.Add, _ => { }));
        Assert.Empty(recycled);
    }

    [Fact]
    public void clone_nre_male_null_ridetpl_a_failing_removal_is_warned_and_the_original_failure_still_surfaces()
    {
        var net = new CloneOrphanNet();
        var warned = new List<string>();
        var boom = new InvalidOperationException("clone");
        var thrown = Assert.Throws<InvalidOperationException>(() => net.Run(() =>
        {
            net.Record(new object());
            throw boom;
        }, _ => throw new InvalidOperationException("recycle"), warned.Add));
        Assert.Same(boom, thrown);
        Assert.Single(warned);
        Assert.Contains("recycle", warned[0]);
    }

    [Fact]
    public void clone_nre_male_null_ridetpl_only_the_first_model_made_during_the_call_is_the_copy()
    {
        var net = new CloneOrphanNet();
        var first = new object();
        var recycled = new List<object>();
        Assert.Throws<InvalidOperationException>(() => net.Run(() =>
        {
            net.Record(first);
            net.Record(new object());
            throw new InvalidOperationException();
        }, recycled.Add, _ => { }));
        Assert.Equal(new[] { first }, recycled);
    }
}
