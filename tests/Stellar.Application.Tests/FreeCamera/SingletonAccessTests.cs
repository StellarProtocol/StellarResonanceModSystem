using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// docs/il2cpp-probing-safety.md: a ZSingleton<T>.Instance getter may CREATE the singleton. Reads gate on IsCreated.
public sealed class SingletonAccessTests
{
    private sealed class NotCreated
    {
        public static int InstanceReads;
        public static bool IsCreated => false;
        public static NotCreated Instance { get { InstanceReads++; return new NotCreated(); } }
    }

    private sealed class Created
    {
        public static readonly Created One = new();
        public static bool IsCreated => true;
        public static Created Instance => One;
    }

    private sealed class Plain
    {
        public static readonly Plain One = new();
        public static Plain Instance => One;
    }

    [Fact]
    public void Never_reads_Instance_while_IsCreated_is_false()
    {
        var s = new SingletonAccess();
        Assert.True(s.Resolve(typeof(NotCreated)));
        Assert.Null(s.Get());
        Assert.Equal(0, NotCreated.InstanceReads);
    }

    [Fact]
    public void Returns_the_instance_when_created_or_ungated()
    {
        var a = new SingletonAccess();
        a.Resolve(typeof(Created));
        Assert.Same(Created.One, a.Get());
        var b = new SingletonAccess();
        b.Resolve(typeof(Plain));
        Assert.Same(Plain.One, b.Get());
    }

    [Fact]
    public void Unresolved_returns_null() => Assert.Null(new SingletonAccess().Get());
}
