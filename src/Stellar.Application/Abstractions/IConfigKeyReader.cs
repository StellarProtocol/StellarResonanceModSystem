using System.Collections.Generic;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Framework-INTERNAL companion to <see cref="Stellar.Abstractions.Services.IConfigSection"/> that
/// enumerates persisted keys by prefix (the read-side mirror of
/// <see cref="Stellar.Abstractions.Services.IConfigSection.RemoveByPrefix"/>).
/// </summary>
/// <remarks>
/// Kept OFF the public <c>IConfigSection</c> surface on purpose: plugins consume config, they never need
/// to enumerate raw flat keys, and the public plugin interface must stay narrow. The concrete
/// <c>ConfigSection</c> implements this alongside <c>IConfigSection</c>; a consumer that needs key
/// enumeration (e.g. <c>NativeUiService</c>'s closest-resolution fallback) does a
/// <c>_config is IConfigKeyReader</c> check and degrades gracefully when it isn't available (test doubles
/// opt in explicitly).
/// </remarks>
internal interface IConfigKeyReader
{
    /// <summary>Every key in the section whose name begins with <paramref name="prefix"/> (ordinal). A
    /// snapshot — safe to enumerate while the caller reads other values. Never throws.</summary>
    IEnumerable<string> KeysWithPrefix(string prefix);
}
