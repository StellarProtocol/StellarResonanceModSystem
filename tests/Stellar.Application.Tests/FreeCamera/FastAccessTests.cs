using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

public sealed class FastAccessTests
{
    private sealed class Box
    {
        public float Value { get; set; }
        public object? Child { get; set; }
        public object? Find(long id) => id == 7 ? Child : null;
    }

    [Fact]
    public void Compiled_getter_and_setter_round_trip()
    {
        var p = typeof(Box).GetProperty(nameof(Box.Value))!;
        var get = FastAccess.Getter<float>(p)!;
        var set = FastAccess.Setter<float>(p)!;
        var b = new Box();
        set(b, 2.5f);
        Assert.Equal(2.5f, b.Value);
        Assert.Equal(2.5f, get(b));
    }

    [Fact]
    public void Reference_getter_returns_the_object()
    {
        var child = new object();
        var get = FastAccess.Getter<object?>(typeof(Box).GetProperty(nameof(Box.Child)))!;
        Assert.Same(child, get(new Box { Child = child }));
    }

    [Fact]
    public void Missing_property_gives_null() => Assert.Null(FastAccess.Getter<float>(null));

    // The hold looks every held entity up by uuid each frame (ZEntityMgr.GetEntity) — compiled, not MethodInfo.Invoke.
    [Fact]
    public void Compiled_one_argument_call_returns_the_result()
    {
        var child = new object();
        var find = FastAccess.Func1<long, object?>(typeof(Box).GetMethod(nameof(Box.Find)))!;
        var b = new Box { Child = child };
        Assert.Same(child, find(b, 7));
        Assert.Null(find(b, 8));
    }

    [Fact]
    public void Missing_method_gives_null() => Assert.Null(FastAccess.Func1<long, object?>(null));
}
