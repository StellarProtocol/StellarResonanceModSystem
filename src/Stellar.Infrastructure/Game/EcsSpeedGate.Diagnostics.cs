using System.Threading;
using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

/// <summary>Diagnostics only: how often each ECS speed writer's prefix ran while armed (review 2026-10-02: the prefix cost
/// was unmeasured). <see cref="CountCall"/> returns on its first line unless <c>StellarDiagnostics.IsEnabled</c>; an unarmed
/// prefix never reaches it. Printed on the freeze <c>summary:</c> line as <c>ecsCalls=LD:&lt;n&gt;,PS:&lt;n&gt;,PC:&lt;n&gt;,PD:&lt;n&gt;</c>
/// (<c>SetAnimatorLayerData</c> / <c>PlayState</c> / <c>PlayClip</c> / <c>PlayDynamicState</c>); divided by the summary's
/// <c>ms=</c> it is the real per-second hook rate. Interlocked: the writers can run off the main thread.</summary>
internal sealed partial class EcsSpeedGate
{
    /// <summary>The four patched writers, in <see cref="CallCountsText"/>'s order.</summary>
    internal enum Writer { LayerData, PlayState, PlayClip, PlayDynamic }

    private readonly int[] _calls = new int[4];

    /// <summary>One armed prefix call of <paramref name="writer"/> (diagnostics only).</summary>
    public void CountCall(Writer writer)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        Interlocked.Increment(ref _calls[(int)writer]);
    }

    /// <summary>The armed prefix calls of <paramref name="writer"/> this freeze (0 with diagnostics off).</summary>
    public int Calls(Writer writer) => Volatile.Read(ref _calls[(int)writer]);

    public string CallCountsText() =>
        $"LD:{Calls(Writer.LayerData)},PS:{Calls(Writer.PlayState)},PC:{Calls(Writer.PlayClip)},PD:{Calls(Writer.PlayDynamic)}";

    private void ResetCallCounts()
    {
        for (var i = 0; i < _calls.Length; i++) Volatile.Write(ref _calls[i], 0);
    }
}
