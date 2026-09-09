using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.Application.Tests.Domain;

/// <summary>Pins the P2 rDPS attribute event (spec § 6.1): an additive CombatEvent case carrying the
/// stored scalar attrs of ONE entity from ONE wire packet, stamped with that packet's receive time.</summary>
public sealed class EntityAttributesChangedTests
{
    [Fact]
    public void Carries_entity_timestamp_and_attr_pairs()
    {
        var attrs = new List<AttrValue> { new(11710, 3350), new(12670, 1200) };
        var ev = new CombatEvent.EntityAttributesChanged(1_788_604_960_970L, new EntityId(0x0000_0001_0000_0280), attrs);
        Assert.Equal(1_788_604_960_970L, ev.TimestampMs);
        Assert.True(ev.TargetId.IsPlayer);
        Assert.Equal(2, ev.Attrs.Count);
        Assert.Equal(11710, ev.Attrs[0].AttrId);
        Assert.Equal(3350L, ev.Attrs[0].Value);
    }

    [Fact]
    public void Is_a_CombatEvent_case_so_existing_subscribers_can_pattern_match()
    {
        CombatEvent ev = new CombatEvent.EntityAttributesChanged(1L, new EntityId(640), new List<AttrValue>());
        Assert.True(ev is CombatEvent.EntityAttributesChanged);
        Assert.False(ev is CombatEvent.BuffChanged);
    }
}
