using System;

namespace Stellar.Abstractions.Diagnostics;

/// <summary>
/// Marks a <b>framework</b> method that touches live game state and MUST self-gate on
/// <see cref="Services.IClientState.IsWorldActive"/>. The <c>Stellar.Analyzers</c> STELLAR0006 rule fails the
/// build if a method carrying this attribute lacks an <c>if (!…IsWorldActive) return;</c> early-return guard.
///
/// <para>Rationale: a framework game-state unit that forgets its guard corrupts the world-connect handshake
/// (everyone disconnects), whereas a plugin that forgets its own gate harms only itself. The analyzer therefore
/// targets the framework's own units (it runs on <c>src/</c> only, never plugin projects). This attribute is a
/// no-op at runtime — purely a compile-time contract marker.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class WorldGatedAttribute : Attribute
{
}
