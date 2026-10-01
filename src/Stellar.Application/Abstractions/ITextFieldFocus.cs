namespace Stellar.Application.Abstractions;

/// <summary>Whether a Stellar overlay text field holds keyboard focus — the same source the Host's keyboard gate reads
/// (<c>WindowService.AnyFieldFocused</c>). Lets an input-shield holder ignore keys typed into a panel. Main thread.</summary>
internal interface ITextFieldFocus
{
    /// <summary>True while any mounted window has a focused text field.</summary>
    bool AnyFieldFocused { get; }
}
