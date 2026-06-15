# Disclaimer

StellarResonance is an **independent, fan-made interoperability framework**. It is **not
affiliated with, authorized by, endorsed by, or connected to** the publisher, developer, or
any rights-holder of the game it interoperates with. All game names, trademarks, and
copyrights are the property of their respective owners; they are referenced here only for
factual identification of compatibility.

## No game code or assets

This repository contains **no material derived from the game**:

- no decompiled or disassembled game code,
- no Cpp2IL / IL2CPP dumps or `global-metadata` output,
- no generated `Panda.*` / `Assembly-CSharp` interop assemblies,
- no game art, audio, models, text, or asset packages.

The framework builds against IL2CPP interop assemblies that **you generate locally from your
own legally-obtained copy of the game**. Those generated assemblies are never committed,
distributed, or redistributed by this project.

## Scope — quality-of-life only

The framework provides a **read-only** service surface for client-side quality-of-life
modifications (UI overlays, HUDs, chat tooling, log viewers). It does **not** provide, and
will not accept contributions providing: packet construction or modification, memory
read/write primitives, automation that creates unfair advantage, or anti-cheat evasion. See
the README for the full policy.

## Your responsibility

Modifying a game client may violate that game's Terms of Service. You install and run this
software **at your own risk**; any action taken against your account by the publisher is your
responsibility. This is experimental software provided **without warranty** of any kind.

## Takedown

This project respects intellectual-property rights. If you are a rights-holder and believe
any content here infringes, please open an issue or contact the maintainers; we will respond
promptly.
