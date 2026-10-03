using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game.Posing;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// Recon run 4: the joystick point (±0.6 m right, 0.5 m up of the head, local z 0.2); pause/scrub = fraction × the
// action's length (9020 "Dance I" = 4.80 s → 0.4 = 1.92 s, measured exact); FaceDataId[1] male / [2] female.
public sealed class PoseMathTests
{
    [Fact]
    public void Aim_offset_scales_and_clamps()
    {
        var (r, u) = PoseMath.AimOffset(0.5f, 0.5f);
        Assert.Equal(0.3, r, 3);
        Assert.Equal(0.25, u, 3);
        (r, u) = PoseMath.AimOffset(2f, -2f);
        Assert.Equal(0.6, r, 3);
        Assert.Equal(-0.5, u, 3);
        Assert.Equal(0.2f, PoseMath.LocalDepth);
    }

    [Fact]
    public void Persist_time_is_the_fraction_of_the_length_and_minus_one_plays()
    {
        Assert.Equal(-1f, PoseMath.PersistTime(-1f, 4.8f));
        Assert.Equal(1.92, PoseMath.PersistTime(0.4f, 4.8f), 3);
        Assert.Equal(4.8, PoseMath.PersistTime(1.5f, 4.8f), 3);
        Assert.Equal(0f, PoseMath.PersistTime(0f, 4.8f));
    }

    [Fact]
    public void Fraction_reads_back_and_is_unknown_without_a_length()
    {
        Assert.Equal(0.4, PoseMath.Fraction(1.92f, 4.8f), 3);
        Assert.Equal(1f, PoseMath.Fraction(6f, 4.8f));
        Assert.Equal(-1f, PoseMath.Fraction(1f, 0.05f));
    }

    [Fact]
    public void Total_prefers_the_models_own_readback()
    {
        Assert.Equal(4.8f, PoseMath.Total(4.8f, 5f));
        Assert.Equal(5f, PoseMath.Total(0f, 5f));
    }

    [Fact]
    public void Face_id_follows_the_models_gender()
    {
        var angry = new ExpressionInfo(1003, "Angry", 303, 403);
        Assert.Equal(403, PoseMath.FaceId(angry, 2));
        Assert.Equal(303, PoseMath.FaceId(angry, 1));
        Assert.Equal(303, PoseMath.FaceId(angry, 0));
    }
}
