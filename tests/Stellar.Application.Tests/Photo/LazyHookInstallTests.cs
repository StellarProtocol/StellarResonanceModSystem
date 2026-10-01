using Stellar.Infrastructure.Hooks;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Review fix 2: render-quality / time-of-day game hooks install once, on the first Request / Pin — never at boot.
public sealed class LazyHookInstallTests
{
    [Fact]
    public void Arming_alone_installs_nothing()
    {
        var runs = 0;
        var l = new LazyHookInstall();
        l.Arm(() => runs++);
        Assert.Equal(0, runs);
        Assert.False(l.IsInstalled);
    }

    [Fact]
    public void First_request_after_arming_installs_once()
    {
        var runs = 0;
        var l = new LazyHookInstall();
        l.Arm(() => runs++);
        l.Request();
        l.Request();
        Assert.Equal(1, runs);
        Assert.True(l.IsInstalled);
    }

    [Fact]
    public void A_request_before_arming_installs_when_armed()
    {
        var runs = 0;
        var l = new LazyHookInstall();
        l.Request();
        Assert.Equal(0, runs);
        l.Arm(() => runs++);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Arming_twice_never_installs_twice()
    {
        var runs = 0;
        var l = new LazyHookInstall();
        l.Arm(() => runs++);
        l.Request();
        l.Arm(() => runs++);
        Assert.Equal(1, runs);
    }
}
