// src/Stellar.Application/Services/RegisteredColorSlot.cs
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

internal sealed class RegisteredColorSlot : IColorSlot
{
    private readonly IColorResolver _resolver;
    private readonly IColorRegistry _registry;
    private readonly string _key;
    private bool _disposed;

    public RegisteredColorSlot(IColorResolver resolver, IColorRegistry registry, string key)
    {
        _resolver = resolver;
        _registry = registry;
        _key = key;
    }

    public ColorRgba Value => _resolver.Resolve(_key);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registry.Unregister(_key);
    }
}
