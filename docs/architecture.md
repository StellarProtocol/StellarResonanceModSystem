# Framework architecture

**Status: implemented as `src/Stellar.{Abstractions,Application,Infrastructure,Host}/`.**
This document records the architectural decisions and the discoveries that drove them.

## Critical discovery from the IL2CPP dump: **HybridCLR**

`HybridCLR.Runtime.dll` is loaded into the game. HybridCLR is a hot-update framework that lets Unity IL2CPP games load additional managed C# assemblies at runtime — bypassing the normal "everything must be AOT-compiled" IL2CPP restriction. This is unusual and very important.

Evidence:
- `Assembly-CSharp.dll` is only **50.5 KB** in the IL2CPP dump — almost no game code is compiled in. Everything substantive lives in HybridCLR-loaded hot-update DLLs that ship via the patcher / CDN.
- The `Panda.AOT.*` assembly prefix indicates the AOT-compiled "anchor" stubs HybridCLR needs; the actual `Panda.*` game logic (`Panda.Hud`, `Panda.Script`, `Panda.Table`, `Panda.ZRpcGen`, etc.) is the hot-update side.
- Hot-update payloads ship inside `StreamingAssets/container/m*.pkg` packages, decrypted/loaded by `HybridCLR.Runtime` at boot.

### Why this matters for our framework

Compared to a typical IL2CPP game where everything is native AOT and you fight Il2CppInterop for every method call:

| | Typical IL2CPP game | Star Resonance (HybridCLR) |
|---|---|---|
| Game logic representation | Native AOT, accessed via Il2CppInterop proxies | Interpreted/JIT'd managed CIL, like a Mono game |
| Hooking game methods | Native function hook via MinHook + Il2CppInterop trampolines | **HarmonyX directly**, like Dalamud patches FFXIV |
| Access to game types | Generated proxy wrappers | Direct `Type.GetType` / reflection works |
| Patches survive game updates | Brittle (signatures shift) | More robust — HarmonyX matches by name/signature |

**This makes the target much more Dalamud-shaped than initially assumed.** The actual technique stops being "wrap IL2CPP" and starts being "wait for HybridCLR to finish, then HarmonyX-patch as if it were a Mono game." Empirically confirmed: all six HarmonyX patches on `Panda.Core.Game.*` succeed on the hot-update managed code.

## Stack decision

| Layer | Choice | Reason |
|---|---|---|
| Loader / process injection | **BepInEx 6 IL2CPP (be.755)** | Most mature for Unity 2022 LTS IL2CPP. Loads before HybridCLR initializes, so we can sequence our framework startup to wait for hot-update load. |
| Native interop | **Il2CppInterop** | Only needed for the Unity engine surface (UnityEngine.*) and AOT stubs — most game logic doesn't need it. |
| Method patching | **HarmonyX** | Works directly on HybridCLR-loaded managed assemblies. Same library Dalamud uses. |
| Overlay | **Native Unity uGUI** — a canvas hierarchy driven by `HudService` + `WindowService` | Renders through the game's own Canvas/UI system with zero native hooks. (The original v0.2 shipping path used Unity IMGUI via an injected `OverlayBehaviour.OnGUI`; that was deleted in Phase E because the per-frame `OnGUI` crossing cost ~13 fps.) |
| Plugin DI | Custom service locator (`IPluginServices`) | Game uses VContainer internally; we expose a small, explicit surface rather than wrapping the container directly. |
| Event bus to plugins | Composed `IGameEventBridge` strategy: MessagePipe (preferred, currently disabled) → HarmonyX fallback | Plugins use the same `IGameEvents.Subscribe(typeName, handler)` API regardless of which bridge is active. |

The overlay is built from native uGUI: plugins register windows via `IWindowHost` (backed by `WindowService`) and HUD elements via `IHudHost` (backed by `HudService`), composing element trees rather than issuing immediate-mode draw calls.

## Component layout

Clean Architecture across five assemblies plus samples. Dependency rule is **enforced** by project references + `InternalsVisibleTo`:

```
src/
├── Stellar.Abstractions/    plugin-facing contracts (public). Zero external deps.
├── Stellar.Wire/            internal wire protocol (frame parse / stub routing / method IDs).
│                            depends on: Abstractions (BCL + Abstractions only)
├── Stellar.Application/     services + outbound interfaces (internal).
│                            depends on: Abstractions
├── Stellar.Infrastructure/  adapters: BepInEx / HarmonyX / Unity IL2CPP / MessagePipe.
│                            depends on: Abstractions, Application, Wire + external runtimes
├── Stellar.Host/            composition root. The only place that says `new ConcreteThing(...)`.
│                            depends on: Abstractions, Application, Infrastructure
└── samples/
    └── Stellar.DebugInfo/   reference plugin. depends on: Abstractions only.
```

Plugin authors reference `Stellar.Abstractions.dll` only. They physically cannot touch internals — the compiler stops them.

## Bootstrap sequence (high level)

The startup sequence at a glance:

1. BepInEx loads `Stellar.Host` (and the .NET runtime resolves the other three framework DLLs).
2. `BootstrapPlugin.Load()` constructs all services and adapters, wires them via constructor injection.
3. `AppDomainHotUpdateWatcher` waits for all 8 hot-update Panda assemblies to load.
4. On all-loaded: create the native uGUI canvas hierarchy (`HudService` + `WindowService` attach), apply 6 HarmonyX postfix patches on `Panda.Core.Game.*` lifecycle methods, load user plugins from `<game>/stellar/plugins/**/*.dll`.

## What's deliberately out of scope

- **Packet modification.** Read-only inspection only. Even without anti-cheat, sending forged packets to a live server is the line between QoL and exploitation. See [`README.md`](../README.md) for the full QoL-only stance.
- **Cracking the `m*.pkg` container format.** Not needed — HybridCLR will load DLLs into memory and HarmonyX patches them there.
- **Cross-version compatibility shims.** Re-run recon after each patch instead; the framework targets one game version at a time.

## Open questions (post-v0.2)

1. **MessagePipe container path.** `Panda.Core.Game.GameRoot` does not expose the VContainer `IObjectResolver`. Where is it?
2. **Friendly scene names.** `OnEnterScene` delivers numeric scene IDs (`"1"`, `"7"`). Looking up names probably requires reading from `Panda.Table` data.
3. **Overlay technology — RESOLVED.** The original v0.2 question asked whether Unity IMGUI would prove limiting (no images, ugly styling). It did, and it also cost ~13 fps via the per-frame `OnGUI` crossing. Resolved in Phase E: the IMGUI overlay was deleted and the framework migrated to native Unity uGUI. A Dear ImGui DX12 swapchain hook is no longer being considered.
