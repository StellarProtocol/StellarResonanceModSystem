namespace Stellar.Abstractions.Services;

/// <summary>
/// Plugin-facing service for injecting declarative mod uGUI into curated game-UI
/// anchors. The framework builds, styles, and lifecycle-manages the real uGUI;
/// plugins only describe intent via <see cref="NativeUiElementSpec"/>.
/// </summary>
public interface INativeUiHost
{
    /// <summary>Register an element. Injected when its anchor is present; returns a handle to manage it.</summary>
    INativeUiElementHandle Register(NativeUiElementSpec spec);
}

/// <summary>Handle to a registered element. Auto-removed on plugin/framework dispose.</summary>
public interface INativeUiElementHandle
{
    /// <summary>True while the element is live under a present anchor.</summary>
    bool IsInjected { get; }
    /// <summary>Force an immediate re-pull of dynamic content (Indicator/Panel) between refresh ticks.</summary>
    void Update();
    /// <summary>Remove the element now and stop re-injecting it.</summary>
    void Remove();
}
