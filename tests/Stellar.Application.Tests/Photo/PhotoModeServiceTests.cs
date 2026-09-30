using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class PhotoModeServiceTests
{
    private sealed class FakeProbe : IPhotoModeProbe
    {
        public event Action<PhotoModeKind>? KindChanged;
        public event Action<bool>? CutsceneChanged;
        public void Raise(PhotoModeKind k) => KindChanged?.Invoke(k);
        public void Cut(bool v) => CutsceneChanged?.Invoke(v);
    }

    [Fact]
    public void Cutscene_changes_are_deduplicated()
    {
        var p = new FakeProbe();
        var s = new PhotoModeService(p);
        var seen = new List<bool>();
        s.CutsceneChanged += seen.Add;
        p.Cut(true); p.Cut(true); p.Cut(false);
        Assert.Equal(new[] { true, false }, seen);
        Assert.False(s.InCutscene);
    }

    [Fact]
    public void Raises_entered_and_exited_once_per_transition()
    {
        var p = new FakeProbe();
        var s = new PhotoModeService(p);
        var entered = new List<PhotoModeKind>(); var exited = 0;
        s.Entered += entered.Add; s.Exited += () => exited++;
        p.Raise(PhotoModeKind.Selfie);
        p.Raise(PhotoModeKind.Selfie);
        Assert.True(s.IsActive);
        p.Raise(PhotoModeKind.None);
        p.Raise(PhotoModeKind.None);
        Assert.Equal(new[] { PhotoModeKind.Selfie }, entered);
        Assert.Equal(1, exited);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Switching_kind_exits_then_enters()
    {
        var p = new FakeProbe();
        var s = new PhotoModeService(p);
        var log = new List<string>();
        s.Entered += k => log.Add("in:" + k); s.Exited += () => log.Add("out");
        p.Raise(PhotoModeKind.CameraFrame);
        p.Raise(PhotoModeKind.Selfie);
        Assert.Equal(new[] { "in:CameraFrame", "out", "in:Selfie" }, log);
    }
}
