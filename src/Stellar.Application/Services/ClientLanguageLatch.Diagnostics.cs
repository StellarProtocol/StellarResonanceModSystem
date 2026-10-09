using System;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Application.Services;

internal sealed partial class ClientLanguageLatch
{
    private int _provisionalReads;

    // STELLAR_DIAGNOSTICS: who reads the client language before it is known (the first reader's stack, once) and how
    // often — the evidence for the 2.21.0 "follow latched zh_Hans at boot" root cause.
    private void NoteProvisionalRead()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (++_provisionalReads == 1)
            _log.Info($"[Stellar][GameData][diag] client language read before known (served 'en'); first reader:{Environment.NewLine}{Environment.StackTrace}");
    }

    // STELLAR_DIAGNOSTICS: every latch signal with its raw read, so a boot log shows which game signal set it.
    private void NoteSignal(int raw, bool allowDefault, string source)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][GameData][diag] client language signal source={source} raw={raw} allowDefault={allowDefault} latched={_index} provisionalReads={_provisionalReads}");
    }
}
