using Xunit;

namespace Stellar.Application.Tests.Diagnostics;

// PerfProbe and PerfControls are process-global statics. Any test class that
// mutates them via the internal test seam must join this collection so xUnit
// serialises execution and prevents concurrent dictionary corruption.
[CollectionDefinition("PerfStaticState", DisableParallelization = true)]
public sealed class PerfStaticStateCollection { }
