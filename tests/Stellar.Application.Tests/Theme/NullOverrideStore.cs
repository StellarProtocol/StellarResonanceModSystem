// tests/Stellar.Application.Tests/Theme/NullOverrideStore.cs
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;

namespace Stellar.Application.Tests.Theme;

internal sealed class NullOverrideStore : IColorOverrideStore
{
    public bool TryGet(string themeName, string slotKey, out ColorRgba value) { value = default; return false; }
    public void Set(string themeName, string slotKey, ColorRgba value) { }
    public void Clear(string themeName, string slotKey) { }
    public bool Has(string themeName, string slotKey) => false;
    public void Flush() { }
    public bool RenameTheme(string oldName, string newName) => false;
    public bool RemoveTheme(string themeName) => false;
}
