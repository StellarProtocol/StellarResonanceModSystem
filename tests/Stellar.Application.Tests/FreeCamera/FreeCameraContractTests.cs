using System;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Pins the 2.14.0 plugin surface (spec 2026-10-01-photo-studio-free-camera-design.md § 6): every contract stays within
// the 8-member interface budget and the release reasons plugins switch on keep their names.
public sealed class FreeCameraContractTests
{
    private static int Members(Type t) =>
        t.GetMethods().Count(m => !m.IsSpecialName) + t.GetProperties().Length + t.GetEvents().Length;

    [Theory]
    [InlineData(typeof(ICameraOverride), 4)]
    [InlineData(typeof(ICameraControl), 5)]
    [InlineData(typeof(IInputShield), 2)]
    [InlineData(typeof(IInputShieldHandle), 7)]
    [InlineData(typeof(ISceneFreeze), 4)]
    [InlineData(typeof(IEmotes), 3)]
    [InlineData(typeof(ICombatState), 2)]
    [InlineData(typeof(IEntityPicker), 1)]
    public void Contract_member_counts_are_pinned(Type contract, int expected)
    {
        Assert.Equal(expected, Members(contract));
        Assert.True(Members(contract) <= 8, $"{contract.Name} exceeds the 8-member budget");
    }

    [Fact]
    public void Framework_version_is_2_14_0() => Assert.Equal("2.14.0", FrameworkVersion.Value);

    [Fact]
    public void Release_reasons_are_stable() => Assert.Equal(
        new[] { "Disposed", "SceneChanged", "Cutscene", "GamePhotoMode", "Disconnected", "PluginUnloaded", "Error" },
        Enum.GetNames(typeof(CameraReleaseReason)));

    [Fact]
    public void Camera_control_and_shield_handle_are_disposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(ICameraControl)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IInputShieldHandle)));
    }
}
