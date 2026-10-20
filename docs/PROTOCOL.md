# sys-botbase protocol notes

These notes describe how sys-botbase behaves on the wire, written in our own words from
reading upstream source and documentation on 2026-10-19:

- [olliz0r/sys-botbase](https://github.com/olliz0r/sys-botbase) at `ae13548` (2026-06-02), reports version **2.5**.
- [PokemonAutomation/sys-botbase-cpp](https://github.com/PokemonAutomation/sys-botbase-cpp) (last push 2026-03-22), reports **3.33** (or 3.32, see below).
- [kwsch/SysBot.NET](https://github.com/kwsch/SysBot.NET) `SysBot.Base` (client behaviour only).

No upstream code is copied. See [LICENSING-RESEARCH.md](LICENSING-RESEARCH.md).

> **Verification status:** everything here comes from reading source, not from hardware.
> Items marked ⚠ are especially worth confirming on a real Switch. The hardware checklist is
> [HARDWARE-VALIDATION.md](HARDWARE-VALIDATION.md).

## Transport

| Property | sys-botbase 2.x | sys-botbase-cpp 3.x |
| --- | --- | --- |
| Transport | TCP | TCP **or** USB, chosen in `atmosphere/contents/430000000000000B/config.cfg` |
| Port | 6000, hard-coded, all interfaces | 6000 |
| Clients | Many at once (poll loop), commands handled one at a time | **One at a time.** Extra connections are left unanswered in the backlog |
| Startup | Sleeps ~20 s after boot before initialising, flashes Joy-Con LEDs when ready | Similar |
| Command terminator | Reads until `\n`; `\r` is stripped by the tokenizer | Splits on **`\r\n` only** |
| Response terminator | `\n` | `\n` (some responses, for example `getVersion`, end `\r\n`) |
| Max command line | 21,888 bytes. **No bounds check** on the read buffer | Dynamic buffer |
| Per-command pacing | Main loop sleeps `mainLoopSleepTime` (default **50 ms**) after every command | Separate command/sender threads with bounded queues. Commands are dropped if a queue is full |

**NX Hub's choices:**

- Always send **`\r\n`**. It works for both implementations.
- Accept `\n` or `\r\n` on responses.
- Never send more than 4,096 bytes per line (enforced in `SysBotCommand`).
- Treat NX Hub as the **only** client per Switch (required by the cpp rewrite, and good gateway
  hygiene anyway). Diagnostics reuse the live session instead of opening a second socket.

## Framing and correlation

There is **no request ID**. A response is simply the next line on the socket. Some commands
produce a line and many produce **nothing at all**:

| Produces a response line | Produces nothing |
| --- | --- |
| `getVersion`, `getTitleID`, `getTitleVersion`, `getBuildID`, `getSystemLanguage`, `getHeapBase`, `getMainNsoBase`, `isProgramRunning`, `charge`, `peek*`, `pointer*` (except `pointerPoke`), `pixelPeek`, `freezeCount`, `game`, cpp `ping` | `press`, `release`, `click`, `setStick`, `detachController`, `touch*`, `key*`, `poke*`, `pointerPoke`, `configure`, `freeze`/`unFreeze`/…, `screenOn/Off`, **any unknown command** |

Consequences:

1. **No acknowledgement for input.** A `press A` that reached the socket cannot be confirmed by
   the protocol. NX Hub's diagnostics confirm the module is still responsive after an input, and
   the hardware checklist confirms the visible effect.
2. **Unknown commands are silent.** Probing whether a command exists makes you wait for a
   timeout, and then the stream is ambiguous. NX Hub never probes. Capabilities are inferred
   from `getVersion` (see below).
3. **A timeout poisons the connection.** If a response arrives late, it would be read as the
   answer to the next command. NX Hub's `SysBotBaseClient` marks itself faulted after any
   timeout or I/O error. The session discards it and reconnects.
4. **Echo mode breaks pairing.** `configure echoCommands 1` makes the module repeat every
   command. NX Hub sends `configure echoCommands 0` right after connecting. It is harmless when
   echo is already off.
5. **`clickSeq` (2.x) prints `done` asynchronously** from another thread, which can interleave
   with other responses. NX Hub does not use `clickSeq`.

## Version and capability detection

`getVersion` is the only identification command common to both:

| Response | Interpretation |
| --- | --- |
| `2.x` (currently `2.5`) | olliz0r sys-botbase. `clickSeq`, `freeze*` and touch/keyboard available. No `ping`. |
| `3.33` | sys-botbase-cpp with backwards compatibility **on** (default). Hex-encoded text responses. `ping`, tick-precise `cq*` controller queue. No `clickSeq`/`freeze`. |
| `3.32` | sys-botbase-cpp with backwards compatibility **off**: responses become **raw binary** (a title ID can contain `0x0A`), which breaks line framing. NX Hub warns. Do not use this mode with NX Hub. |
| anything else | Unknown. NX Hub assumes buttons and sticks only and warns. |

This mapping is a heuristic based on current upstream. Version numbers may change, so keep
`SysBotCapabilities.FromVersion` and its tests up to date.

## Controller

- The first controller command **attaches a virtual Pro Controller** (`hiddbgAttachHdlsVirtualDevice`).
  The Switch shows it as a new controller ⚠ (it may take a player slot, and some games need it
  to be "connected" via the controller menu).
- Controller state (`buttons`, both sticks) lives in a **global variable inside the module**:
  - it **persists after the client disconnects**. A held button stays held until something
    releases it.
  - `detachController` detaches the device but **does not clear** the stored button bits. The
    next attach re-applies them. (NX Hub's "reset" therefore releases everything first.)
- Button tokens (case-sensitive): `A B X Y L R ZL ZR PLUS MINUS DUP DDOWN DLEFT DRIGHT HOME CAPTURE LSTICK RSTICK`
  (aliases `DU DD DL DR`, plus `PALMA` and `UNUSED`).
  - **2.x maps any unknown token to `A`.** NX Hub only ever sends tokens from a fixed table.
  - cpp ignores unknown tokens.
- `press X` / `release X`: set or clear one bit and push the state immediately.
- `click X`: press, sleep `buttonClickSleepTime` (default 50 ms, a **global** setting), release.
  It blocks the module's main loop for that time. NX Hub uses explicit press/release so each
  call can choose its own duration.
- `setStick LEFT|RIGHT x y`: signed 16-bit values, clamped to `-0x8000..0x7FFF`, decimal or `0x`
  hex. Up is positive Y ⚠.
- Games sample input at 30–60 Hz. Presses shorter than ~2 frames can be missed. NX Hub's
  minimum press is 20 ms and the default is 100 ms.

### Tick-precise controller queue (sys-botbase-cpp only, not used yet)

The cpp rewrite adds `cqControllerState <64 hex chars>`, `cqCancel` and `cqReplaceOnNext`. The
hex encodes a 32-byte little-endian struct: `u64 seqnum`, `u64 milliseconds`, `u64 buttons`,
`i16 lx, ly, rx, ry`. This enables frame-accurate macros independent of network jitter. It is
the natural backend for NX Hub's future macro engine on cpp devices.

## Memory access (not exposed by NX Hub yet)

`peek`/`poke` (heap-relative), `*Absolute`, `*Main` (main NSO-relative), `*Multi` variants and
pointer-chain commands (`pointerPeek`, `pointerAll`, `pointerRelative`, `pointerPoke`). Upstream
comments mark these as unvalidated: bad pointer chains can crash the game or console. NX Hub
deliberately exposes **no** memory API in Milestone 1 (see ARCHITECTURE.md, Security).

`getTitleID`, `getTitleVersion` and `getBuildID` briefly attach the debugger to the running
application to read metadata. That is the same mechanism every SysBot-style bot uses
continuously. NX Hub's diagnostics call `getTitleID` once per run.

## Screenshots

`pixelPeek` returns the current frame as JPEG bytes encoded as one hex line (up to ~1 MB of
text). Upstream notes it can fail with result `0x668CE` unless the debug-unit flag is patched ⚠.
Not exposed in Milestone 1. The client already allows 2 MB response lines for it.

## Latency

Every round trip includes Wi-Fi latency on both legs plus, on 2.x, the 50 ms post-command
sleep. Expect roughly 50–100 ms round trips on a healthy LAN with 2.x ⚠. NX Hub reports latency
from `getVersion` round trips (handshake, heartbeats, diagnostics).

## USB (future)

usb-botbase and sys-botbase-cpp in USB mode use VID `0x057E` / PID `0x3000` with a 4-byte
little-endian length prefix per message. The `ISysBotTransport` abstraction is designed so a USB
transport can be added (libusb on Linux, with a udev rule) without changes above the transport
layer.

## What NX Hub sends today

| Purpose | Commands |
| --- | --- |
| Handshake | `configure echoCommands 0`, `getVersion` |
| Heartbeat / latency | `getVersion` (when idle for `HeartbeatSeconds`) |
| Diagnostics | `getVersion` ×6, `getTitleID`, optional `release <unheld button>` |
| Controller | `press`, `release`, `setStick`, `detachController` |
