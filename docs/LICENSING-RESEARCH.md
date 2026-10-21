# Licensing research

> Status: researched 2026-10-19 against upstream default branches. This is an engineering
> assessment written to guide architecture, **not legal advice**. Re-check before any release
> that bundles third-party code, and get proper legal review if NX Hub is ever distributed
> commercially.

## Summary

| Decision | Outcome |
| --- | --- |
| NX Hub Core licence | **Apache-2.0** (owner decision 2026-10-20; see "Why Apache-2.0" below) |
| sys-botbase client | **Independent implementation** written from the documented wire protocol. No upstream code copied. |
| SysBot.NET (AGPL-3.0) | Studied for behaviour only. **No code copied or linked.** |
| PKHeX.Core (GPL-3.0) | **Not used in Core.** Future optional Pokémon service only, licensed GPL-3.0 itself. |
| PKHeX-Plugins / ALM / LiveHeX (MIT, but built on GPL PKHeX.Core) | Not used in Milestone 1. Future Pokémon service may use them, under GPL-3.0 as a combined work. |
| Pokémon Automation (MIT source, non-commercial binaries) | Studied only. Its tick-precise controller protocol is documented in sys-botbase-cpp and can be spoken by our own client later. |

Nothing in this repository is derived from upstream source code. All protocol knowledge comes
from reading upstream documentation and source to understand **behaviour**, which is then
described in our own words in [`PROTOCOL.md`](PROTOCOL.md) and implemented independently.

## Per-project findings

Licence IDs are from each repository's `LICENSE` file (checked directly, not just GitHub's
detection; GitHub reports PKHeX as `NOASSERTION` but the file is the unmodified GPLv3 text).

### olliz0r/sys-botbase

- **Licence:** GPL-3.0.
- **State:** active (last push 2026-06-02). `getVersion` reports `2.5` at current HEAD.
- **What we care about:** the TCP line protocol: command names, argument syntax, response
  format, and controller semantics.
- **How we use it:** we **communicate with it over the network**. It runs on the Switch as a
  separate program. NX Hub sends text commands to it.
- **Implications:** talking to a GPL program over a socket using its documented protocol does
  not make the client a derivative work. The client is written from scratch, so this is
  independent. We do not ship sys-botbase binaries and we do not copy its source.
- **Recommendation:** independent client implementation (done). Never vendor its C source into
  this repo. If we ever bundle a sys-botbase build for users' convenience, that bundle is a
  separate GPL work shipped alongside (aggregation), with source offer, and must not be linked
  into NX Hub.

### PokemonAutomation/sys-botbase-cpp

- **Licence:** GPL-3.0.
- **State:** active (last push 2026-03-22). C++ rewrite of sys-botbase plus usb-botbase, with
  "backwards compatibility" on by default and new tick-precise `cq*` controller commands.
  `getVersion` reports `3.33` (compat on) or `3.32` (compat off).
- **How we use it:** we **communicate with it** (same as above).
- **Implications:** same as sys-botbase. Protocol facts (command names, struct layouts as data
  formats) are interoperability information. We describe them in our own words and implement
  them ourselves.
- **Recommendation:** independent implementation. Support it as a first-class target alongside
  the original, detected via `getVersion`.

### fishguy6564/USB-Botbase, Koi-3088/usb-botbase

- **Licence:** GPL-3.0 (fishguy6564; last push 2021). The Koi-3088 repository now redirects.
- **How we use it:** study the USB framing for a possible future USB transport. Not used in
  Milestone 1.
- **Recommendation:** independent implementation if/when a USB transport is built. A future USB
  transport on Linux would need libusb access (LibUsbDotNet is LGPL-3.0; consider the licence
  implications at that time).

### kwsch/SysBot.NET

- **Licence:** **AGPL-3.0**.
- **State:** active (last push 2026-09-08).
- **What we care about:** how a mature client drives sys-botbase: `\r\n` terminators, connect
  timeouts, transfer-size chunking, USB framing, and the routine structure.
- **How we use it:** **study behaviour only.**
- **Implications:** AGPL is the strongest copyleft. Copying or linking any SysBot.NET code (for
  example `SysBot.Base`) would make NX Hub Core AGPL, including the network-use clause. That
  is exactly the "accidental licence" outcome we want to avoid.
- **Recommendation:** never copy code, never reference `SysBot.Base` as a package or project.
  Keep our design independent (our API shape, state machine and abstractions are our own).

### kwsch/PKHeX and PKHeX.Core

- **Licence:** GPL-3.0 (plain text, no additional terms found).
- **State:** very active (last push 2026-10-19). `PKHeX.Core` is published on NuGet.
- **What we care about:** PKM parsing, legality analysis, save/box structures, and game
  metadata. This is the foundation of any serious Pokémon tooling.
- **How we would use it:** future **library use**, in-process, inside an optional Pokémon
  component.
- **Implications:** any program that links PKHeX.Core in-process is a combined work under GPL-3.0.
  Distributing it requires the whole combined program to be offered under GPL-3.0.
- **Recommendation:** keep PKHeX.Core **out of NX Hub Core**. See "Pokémon Toolkit boundary".

### architdate/PKHeX-Plugins (Auto-Legality Mod, LiveHeX, PKHeX.Core.Injection)

- **Licence:** MIT (repository), **but** every project references GPL-3.0 `PKHeX.Core`
  (`PKHeX.Core` NuGet `24.5.5` at current HEAD).
- **State:** default branch last pushed 2024-05-08. It appears dormant on GitHub. Check whether
  development moved elsewhere before depending on it.
- **What we care about:**
  - `PKHeX.Core.AutoMod`: Showdown-set → legal PKM generation (ALM).
  - `PKHeX.Core.Injection`: LiveHeX's offsets (`LiveHeXOffsets/*`), pointer cache, per-game
    protocols (`LPBasic`, `LPPointer`, `LPBDSP`, `LPLGPE`), and the sys-botbase/USB connection.
  - `AutoLegalityMod`: WinForms PKHeX plugin UI. **Not useful** for a headless server.
- **How we would use it:** possibly library use inside the future GPL Pokémon service.
- **Implications:** the MIT code itself is permissive, but in practice it cannot run without
  PKHeX.Core, so any distributed binary that uses it is GPL-3.0 as a whole. Per-game offsets
  and pointer chains are facts about games, but large tables of them copied verbatim may still
  carry authorship. Attribute them under MIT terms if reused.
- **Recommendation:** do not use in Core. For the Pokémon service, evaluate using
  `PKHeX.Core.AutoMod` as a library (GPL service) versus re-deriving only what is needed.
  Do not port the WinForms UI.

### arleypadua/PKHeX.Everywhere

- **Licence:** GPL-3.0.
- **State:** active (last push 2026-10-19). A cross-platform web/CLI front-end for PKHeX.Core.
- **What we care about:** proof that PKHeX.Core runs headless/web-hosted, and their approach to
  a browser UI over PKHeX.Core.
- **How we use it:** study only.
- **Recommendation:** useful reference for the future Pokémon service UI. Code reuse only inside
  the GPL Pokémon service, with attribution.

### PokemonAutomation/Arduino-Source (Pokémon Automation SerialPrograms)

- **Licence:** source is MIT "unless otherwise specified". The README states that **precompiled
  binaries are free for non-commercial use only** and that "vanilla GPL is disallowed" as a
  dependency because they reserve the right to relicense.
- **What we care about:** a mature automation framework (vision + controller timing) and the
  tick-precise controller protocol it uses with sys-botbase-cpp.
- **How we use it:** study only.
- **Implications:** do not redistribute their binaries. MIT source could legally be reused with
  attribution, but nothing in Milestone 1 needs it.
- **Recommendation:** treat as prior art. Interoperate via sys-botbase-cpp's documented `cq*`
  commands, implemented ourselves.

### Other projects found during research

| Project | Licence | Notes |
| --- | --- | --- |
| kwsch/SysBot.AnimalCrossing | AGPL-3.0 | Study only. |
| berichan/ACNHMobileSpawner | GPL-3.0 | Study only. |
| ca1e/LiveHex | AGPL-3.0 | Study only. |
| NexusRisen/NexusBot.NET and other SysBot.NET forks | AGPL-3.0 | Study only. |

## Why Apache-2.0 for NX Hub Core

The project briefly started as MIT; the owner chose **Apache-2.0** on 2026-10-20 before anything
was published or any outside contribution was received, so the switch needed no relicensing
consent.

1. **Permissive, so the ecosystem stays open.** Apache-2.0 code can be used by GPL-3.0 and
   AGPL-3.0 projects (the FSF lists Apache-2.0 as compatible with GPLv3). A GPL Pokémon
   service, or someone's AGPL bot, can depend on NX Hub Core; the combined distribution then
   follows the copyleft licence. Had Core been GPL, every plugin, SDK and integration would be
   pushed to GPL.
2. **Explicit patent grant and termination clause**, which MIT lacks. That matters more as
   outside contributions arrive.
3. **Clear contribution terms.** Section 5 makes contributions inbound=outbound under the same
   licence without a CLA.
4. **Ecosystem fit.** MIT-licensed upstream code (PKHeX-Plugins, Pokémon Automation source) may
   still be incorporated into an Apache-2.0 project, as long as its MIT notice is kept.
5. **Trade-off accepted:** Apache-2.0 is **not** compatible with **GPL-2.0-only** code, and
   requires keeping `LICENSE`/`NOTICE` with redistributions. None of the relevant upstream
   projects are GPL-2.0-only (sys-botbase and PKHeX are GPL-3.0).

Obligations for redistributors: ship `LICENSE` and `NOTICE`, and mark modified files. Release
packages built by `scripts/package.sh` include both.

## Pokémon Toolkit boundary (future, not Milestone 1)

**Update, October 2026:** the first Pokémon feature is a trade bot built on SysBot.NET, so it lives
in its own repository, [nx-hub-pokemon](https://github.com/sbd1017/nx-hub-pokemon), licensed
**AGPL-3.0** as a whole (SysBot.NET is AGPL-3.0; PKHeX.Core is GPL-3.0, which AGPL-3.0 may combine
with). NX Hub Core still contains and links no SysBot.NET or PKHeX code. The two talk only over
NX Hub's HTTP API (the hand-over lease), and each keeps its own licence.

The brief proposed:

```text
NX Hub Core (Apache-2.0)
        │  defined local API (HTTP/JSON over loopback or a Unix socket)
        ▼
NX Pokémon Service (GPL-3.0)
        ├── PKHeX.Core (GPL-3.0)
        └── optionally PKHeX.Core.AutoMod / Injection (MIT, combined → GPL)
```

### Does process separation resolve the GPL question?

**Partially. It does not do so automatically.** The relevant considerations:

- The FSF's position (GPL FAQ, "aggregate" and "plug-ins" entries) is that separate programs
  communicating through pipes, sockets or command-line arguments are *normally* separate works.
  However, if the semantics of the communication are intimate enough (for example exchanging
  complex internal data structures), they can be one combined program regardless of the
  mechanism.
- In-process plugins (loading a GPL DLL into the Core process and calling its types directly)
  are treated as a single combined work. A GPL-3.0 plugin loaded into an Apache-2.0 host is
  still *permitted* (Apache-2.0 is GPLv3-compatible), but whoever distributes the combination must do so
  under GPL-3.0 terms.

### Conclusion and recommended design

1. **Core stays Apache-2.0 and never references PKHeX.Core**, not even optionally. This is enforced by
   project structure: no Core project may have a `PackageReference` to `PKHeX.Core`. A CI
   check will be added when the Pokémon service lands.
2. **The Pokémon Toolkit is a separate repository/package licensed GPL-3.0**, honestly and
   deliberately. We do not try to "launder" GPL code through an API boundary. We accept that
   the Pokémon service is GPL.
3. **The boundary is a generic, documented plugin/service API** that is not Pokémon-specific
   (for example: "Core exposes device sessions, memory read primitives under a capability
   model, jobs and events; a service registers UI panels and job types"). Because the same API
   serves non-GPL plugins (Animal Crossing, generic automation), Core is plainly an
   independent work, not a component designed solely around PKHeX internals.
4. **Separate process is preferred for engineering reasons too:** PKHeX.Core is large (memory
   on a Pi), updates on its own cadence, and legality code changes often. A separate service
   can be restarted/updated independently and can crash without taking device sessions down.
5. **Distribution:** official images/installers that bundle both must ship the GPL service's
   source (or an offer) and keep licences clearly separated in the artefact. A "Core-only"
   install must be possible.

## Rules for contributors (also in CONTRIBUTING.md)

- Do not paste code from SysBot.NET (AGPL), sys-botbase (GPL), PKHeX (GPL) or their forks into
  this repository.
- Protocol behaviour may be documented and re-implemented from observation and documentation.
- New dependencies must be MIT/BSD/Apache-2.0/ISC/zlib (or similarly permissive) for Core.
  Anything else needs discussion in an issue first.
- Record every new third-party dependency in [`THIRD-PARTY.md`](../THIRD-PARTY.md).
