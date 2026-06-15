using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.Application.Tests.Domain;

public sealed class KeyBindingTests
{
    [Fact]
    public void ToString_NoModifier_KeyOnly()
    {
        var binding = new KeyBinding(StellarKeyCode.F11);
        Assert.Equal("F11", binding.ToString());
    }

    [Fact]
    public void ToString_WithShift_PrintsShiftPlusKey()
    {
        var binding = new KeyBinding(StellarKeyCode.F12, ModifierKeys.Shift);
        Assert.Equal("Shift+F12", binding.ToString());
    }

    [Fact]
    public void ToString_MultipleModifiers_InCanonicalOrder()
    {
        var binding = new KeyBinding(StellarKeyCode.P, ModifierKeys.Ctrl | ModifierKeys.Alt | ModifierKeys.Shift);
        Assert.Equal("Ctrl+Shift+Alt+P", binding.ToString());
    }

    [Fact]
    public void Equality_SameKeyAndModifiers_AreEqual()
    {
        var a = new KeyBinding(StellarKeyCode.F1, ModifierKeys.Ctrl);
        var b = new KeyBinding(StellarKeyCode.F1, ModifierKeys.Ctrl);
        Assert.Equal(a, b);
    }
}
