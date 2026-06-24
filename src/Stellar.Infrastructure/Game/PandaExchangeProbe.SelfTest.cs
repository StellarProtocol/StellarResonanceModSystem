using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

// Self-test (Phase 1, Task 6 Step 6 discovery). Flag-gated (EXCHANGE_SELFTEST). Re-fires on a timer.
// Sync model-probe runs first (captured even if the async call hangs); then a non-zero-seq async
// care-list with a marker before it. Remove this partial before merge.
internal sealed partial class PandaExchangeProbe
{
    private int _selfTestTick;
    private const int SelfTestEveryTicks = 300;   // ~10s — less UI flicker
    private string? _selfTestLast;

    private const string SelfTestChunk =
        "local out={} local function L(s) out[#out+1]=tostring(s) rawset(_G,\"" + ResultGlobal + "\", table.concat(out,\"\\n\")) end" +
        " if Z==nil or Z.VMMgr==nil then L(\"Z/VMMgr nil\") return end" +
        " local vm=Z.VMMgr.GetVM(\"trade\") if vm==nil then L(\"trade VM nil\") return end" +
        " L(\"=== prime test begin ===\")" +
        " local function ar(n) local f=vm[n] if type(f)~=\"function\" then L(n..\" missing\") return end" +
        "  local ok,i=pcall(debug.getinfo,f,\"u\") if ok and i then L(n..\" nparams=\"..tostring(i.nparams)) end end" +
        " ar(\"OpenTradeMainView\") ar(\"CloseTradeMainView\")" +
        " local oo,oe=pcall(function() return vm:OpenTradeMainView() end)" +
        " L(\"OpenTradeMainView pcall ok=\"..tostring(oo)..\" ret=\"..tostring(oe))" +
        " local seq=(rawget(_G,\"_sxseq\") or 990000)+1 rawset(_G,\"_sxseq\",seq)" +
        " L(\"about to AsyncExchangeCareList(1, \"..seq..\") after prime\")" +
        " ;(Z.CoroUtil.create_coro_xpcall(function()" +
        "  local care=vm:AsyncExchangeCareList(1, seq)" +
        "  L(\"CARE after-prime type=\"..type(care))" +
        "  if type(care)==\"table\" then local n=0 for k,v in pairs(care) do n=n+1 end L(\" care count=\"..n) end" +
        "  local co,ce=pcall(function() return vm:CloseTradeMainView() end)" +
        "  L(\"CloseTradeMainView ok=\"..tostring(co)) end))()";

    private void MaybeRunSelfTest()
    {
        if (!PerfControls.Flag("EXCHANGE_SELFTEST")) return;
        if (_selfTestTick++ % SelfTestEveryTicks == 0) InvokeChunk(SelfTestChunk);
        var dump = ReadLuaGlobalString(ResultGlobal);
        if (dump is null || dump == _selfTestLast) return;
        _selfTestLast = dump;
        foreach (var line in dump.Split('\n'))
            if (line.Length > 0) _log.Info("[Stellar][Exchange][selftest] " + line);
    }
}
