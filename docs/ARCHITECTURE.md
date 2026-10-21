# Architecture

NX Hub is a small, always-on gateway between your network and one or more CFW Switches
running sys-botbase. The core is **game-agnostic**. Game-specific features belong in plugins
(future).

```text
 Browser / iPhone PWA / scripts / Home Assistant / Node-RED
                 │  HTTP (REST + OpenAPI)   WebSocket (/api/events)
                 ▼
 ┌───────────────────────────── NxHub.Server (ASP.NET Core) ─────────────────────────────┐
 │  Token auth · problem-details errors · static web UI · OpenAPI + /docs                │
 │                                                                                       │
 │  ┌──────────────────────────── NxHub.Core (no ASP.NET) ───────────────────────────┐   │
 │  │ SwitchManager ── one SwitchSession per configured Switch                       │   │
 │  │   SwitchSession: supervisor loop (connect → handshake → heartbeat → backoff)   │   │
 │  │   SwitchController: press/hold/release/stick, watchdogs, neutralise on connect │   │
 │  │ DiagnosticsRunner · EventHub · ActivityLog · ISwitchDeviceRepository           │   │
 │  └──────────────────────────────────────┬─────────────────────────────────────────┘   │
 │  SQLite repository (Microsoft.Data.Sqlite)│                                           │
 └───────────────────────────────────────────┼───────────────────────────────────────────┘
                 ┌───────────────────────────▼──────────── NxHub.Protocol ──┐
                 │ SysBotCommands (only place commands are built)           │
                 │ SysBotBaseClient (serialised wire, timeouts, poisoning)  │
                 │ SysBotCapabilities (version → features)                  │
                 │ ISysBotTransport ── TcpSysBotTransport  (USB later)      │
                 └───────────────────────────┬──────────────────────────────┘
                                             │ TCP :6000, CRLF lines
                                             ▼
                                   sys-botbase on the Switch
```

## Projects

| Project | Depends on | Responsibility |
| --- | --- | --- |
| `NxHub.Protocol` | logging abstractions only | Wire protocol: validated commands, response parsing, transports, client. Knows nothing about devices, HTTP or storage. |
| `NxHub.Core` | Protocol | Domain: devices, sessions, controller semantics, diagnostics, events, activity. No ASP.NET dependency, so it can be reused by a CLI or tests. |
| `NxHub.Server` | Core | Host: config, DI, SQLite, auth, REST/WebSocket endpoints, static UI, OpenAPI. |
| `NxHub.Simulator` | — | A TCP sys-botbase imitation for development and tests. Not shipped with releases. |
| `web/` | — | React + TypeScript + Vite UI, built into `src/NxHub.Server/wwwroot`. |

## Key design decisions

### 1. Raw commands are built in exactly one place

`SysBotCommands` is the only factory for wire commands. `SysBotCommand` validates every token
(printable ASCII, no whitespace or control characters, length cap), which prevents command
injection through user input such as a device name or a malformed button. Button names map
through a fixed table (`SysBotBaseInputNames`) because sys-botbase 2.x silently treats unknown
buttons as `A`.

### 2. A timeout poisons the connection

sys-botbase has no request IDs. After any timeout or I/O error the client is **faulted** and
rejects further use. The session then reconnects with a fresh socket. This trades an occasional
reconnect for never mis-pairing a response.

### 3. One supervisor loop per Switch

`SwitchSession` runs a single background loop that owns the connection lifecycle:

```text
           ┌──────────── autoConnect = false ────────────┐
           ▼                                             │
   Disconnected ──connect()──► Connecting ──ok──► Connected ──heartbeat/command fails──┐
           ▲                       │                 │                                 │
           │                       │fail             │disconnect()                     ▼
           │                       ▼                 ▼                           Reconnecting
           └──────────────── Reconnecting ◄── Disconnecting            (backoff 1s,2s,4s… ≤30s, ±20% jitter)
```

- **Heartbeat:** when no response-bearing command has run for `HeartbeatSeconds` (default 10 s),
  the loop sends `getVersion`. This detects dead links and also samples latency.
- **Backoff:** exponential from 1 s up to 30 s with ±20% jitter. It resets after a connection
  has stayed up for 30 s, so a flapping link does not reconnect in a tight loop.
- **Loss detection from commands:** if an API command hits a timeout or socket error, it marks
  the connection lost. The loop wakes immediately rather than waiting for the next heartbeat.
- **Cancellation and shutdown:** disconnect, address change and server shutdown cancel the
  connection's token. All waits use `TimeProvider`, so tests drive time deterministically.
- **Concurrency:** wire access is serialised by a semaphore inside the client. Session state is
  guarded by one lock that is never held across I/O.

### 4. Controls are never left held

sys-botbase keeps controller state inside the Switch, even after the client disconnects. NX Hub
therefore treats "release" as a safety-critical operation:

| Situation | What NX Hub does |
| --- | --- |
| Timed press (`/press`) | Release in `finally`, with its own timeout, even if the HTTP request is cancelled. |
| Explicit hold (`/hold`) | Server-side watchdog auto-releases after `maxHoldMs` (default 30 s). Re-holding refreshes it silently. The web UI refreshes every 4 s while a finger is down and uses a 10 s limit. |
| Stick without duration | Same watchdog, auto-centres. |
| Connection lost while held | Inputs stay tracked as "pending". On reconnect they are released before anything else. |
| Disconnect / delete / shutdown | Best-effort release of tracked inputs (2 s budget) before the socket closes. |
| Web page hidden or closed | The UI releases everything it holds (`visibilitychange`, `pagehide`, `blur`). |
| Unknown state (for example after a crash) | "Release all" sends a release for every button and centres both sticks. `NeutraliseOnConnect=Always` does this on every connect. |

Per-button locks prevent overlapping operations on the same button (for example a tap racing a
release). The UI chains requests per button so a release can never overtake its hold.

### 5. Diagnostics reuse the live connection

sys-botbase-cpp accepts only one client. If the Switch is connected, diagnostics run through the
session. If not, they open a short-lived connection. They never write memory, and they touch the
controller only when explicitly asked (`includeControllerTest`), because the first input attaches
a virtual controller that the Switch will show.

### 6. Multi-Switch from day one

Nothing is a singleton. `SwitchManager` holds a dictionary of sessions keyed by an opaque id
(`sw_…`). Each has its own connection, state, controller tracking, activity log and latest
diagnostics. The API is plural (`/api/switches/{id}/…`). The UI shows a selector as soon as
there is more than one.

### 7. Events

`EventHub` fans out events to WebSocket subscribers with a bounded, drop-oldest buffer per
subscriber, so a slow phone cannot stall device sessions. Event types are listed in
[API.md](API.md#events).

### 8. Macros

`MacroRunner` (one per session) executes a saved or one-off macro on a background task, through
`SwitchController`, so watchdogs and release-in-`finally` apply to every step. One run per Switch;
pause takes effect between steps; stop cancels the current step (a press still releases). When a
run ends for any reason, it releases everything still tracked as held. Macros are stored as JSON in
SQLite (`macros` table, schema v2) and are not tied to a Switch. The web UI's recorder captures
controller input with timestamps and converts it into steps (hold + wait + release becomes a timed
press).

### 9. Live view

`ScreenCapture` serves the newest `pixelPeek` frame, deduplicating concurrent requests and reusing
frames younger than the caller's `maxAge`. Captures run on their own cancellation token: if a viewer's
request were allowed to cancel a half-read response, the connection would have to be discarded (see
decision 2).

### 10. Demo mode

`NxHub:Demo:Enabled` turns an instance into a public demo: an in-process `SysBotSimulator` with an
interactive pretend menu, in-memory storage seeded with one Switch and example macros, no login, and
`DemoGuardMiddleware` refusing configuration changes, saving and disconnecting. Visitor-written
macros run once under a neutral name, so no visitor text is shown to others. It never talks to real
hardware.

### 11. Errors

Every API error is RFC 9457 problem details with a stable `code`. Device-originated failures use
gateway semantics: `502` (connection/protocol) and `504` (timeout). `409` means "not connected"
(or `switch_leased`, see §12).

### 12. Hand-over (leases)

A program that drives sys-botbase itself, such as a trade bot, takes the Switch with
`POST /api/switches/{id}/lease`. The session stops any macro, releases held inputs (the same path as
a disconnect) and stays disconnected; `ExecuteAsync`, connect, macro runs and diagnostics throw
`switch_leased` while the lease is held, so nothing in NX Hub can press a button mid-trade. Leases
have a time to live and are renewed by the holder; a `TimeProvider` timer ends an unrenewed lease,
so a crashed holder can't keep the Switch. When a lease ends the session reconnects only if it was
set to connect before (a disconnect or disabling the Switch during the lease is remembered).

## Security

- **sys-botbase must never be exposed beyond the LAN.** It has no authentication and offers
  arbitrary memory writes. NX Hub is the controlled gateway. Firewall port 6000 if your network
  is shared.
- **API auth:** a single admin bearer token (32 random bytes) generated on first start into
  `<data dir>/admin-token` with mode 0600. It is never logged and is compared in constant time.
  The WebSocket authenticates with its first message, so tokens never appear in URLs or proxy
  logs. `Auth:Mode=Disabled` exists for isolated setups and logs a warning at startup.
- **Bind:** `http://*:8080` by default, so phones on the LAN can reach it. Do not port-forward
  it to the internet. Use a VPN (WireGuard/Tailscale) for remote access. TLS is expected to come
  from a reverse proxy if needed.
- **Input validation:** every request DTO is validated. Request bodies are capped at 64 KB.
  Command tokens are validated again at the protocol layer.
- **No memory API yet.** Arbitrary peek/poke would turn the API into a remote memory editor. A
  future design will gate it behind explicit capabilities, per-plugin scopes and probably a
  separate "dangerous operations" permission.
- **Browser hardening:** CSP (`default-src 'self'`), `X-Frame-Options: DENY`, `nosniff`,
  `no-referrer`. The UI is same-origin only (no CORS).
- **Service hardening:** systemd unit with `DynamicUser`, `ProtectSystem=strict`, an empty
  capability set, and more (`systemd-analyze security` score 3.0 "OK" in testing).

- **Behind a reverse proxy:** NX Hub can run under a sub-path (`PathBase`) and trust a
  proxy-authenticated user header (`TrustedProxyUserHeader`), but only on connections from
  loopback, so LAN clients cannot spoof it. Because such sessions are cookie-backed, every
  state-changing request and the WebSocket must be same-origin, or they get a 403
  `cross_site_request`. `X-Forwarded-Proto` is honoured from loopback only. Example config:
  `deploy/nginx/nxhub-subpath.example.conf`.

Future (not Milestone 1): multiple users/roles, scoped API keys for integrations, an audit log,
and rate limiting on auth failures.

## Plugin runtime (planned)

Not implemented in Milestone 1. Intended shape:

- Plugins register **job types**, **UI panels** and **event handlers** against a versioned SDK
  (`NxHub.Plugins.Abstractions`), and receive a scoped handle to a Switch session (controller,
  capability-gated reads).
- Two hosting modes:
  - **In-process** for permissively licensed plugins.
  - **Out-of-process service** over a local HTTP/gRPC API, for plugins with heavy or copyleft
    dependencies. The Pokémon Toolkit (PKHeX.Core, GPL-3.0) will be this kind. See
    [LICENSING-RESEARCH.md](LICENSING-RESEARCH.md).
- Core never references game-specific code.

## Configuration reference

`appsettings.json` → `NxHub` section, or environment variables such as
`NxHub__Sessions__HeartbeatSeconds=5`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Urls` (top level) | `http://*:8080` | Listen address(es). |
| `NxHub:DataDirectory` | `$STATE_DIRECTORY` or `./data` | SQLite DB and admin token. |
| `NxHub:Auth:Mode` | `Token` | `Token` or `Disabled`. |
| `NxHub:Auth:Token` | generated | Fixed token (≥16 chars) instead of the generated one. |
| `NxHub:Auth:TrustedProxyUserHeader` | off | Header from a reverse proxy that already authenticated the user (e.g. `X-Site-User`). Honoured only from loopback. Changes then also need a same-origin `Origin`/`Sec-Fetch-Site`. |
| `NxHub:Ui:Theme` | `Dark` | Web UI theme: `Dark`, `Light`, or `System` (follow the device). There is no in-app toggle. |
| `NxHub:Ui:Links` | none | Other pages shown in the top bar, each `{Name, Url}` (a site path or http(s) URL), for example the trade bot. A hand-over notice links to the one whose name matches the lease holder. |
| `NxHub:Demo:Enabled` | `false` | Public demo mode (simulated Switch, read-only, no login). |
| `NxHub:PathBase` | off | Serve under a sub-path behind a proxy, e.g. `/nxhub`. Direct access at `/` keeps working. |
| `NxHub:Sessions:ConnectTimeoutSeconds` | 5 | TCP connect + handshake budget. |
| `NxHub:Sessions:ResponseTimeoutSeconds` | 3 | Max wait for a response line. |
| `NxHub:Sessions:HeartbeatSeconds` | 10 | Idle probe interval. Also bounds loss-detection time. |
| `NxHub:Sessions:ReconnectInitialSeconds` | 1 | First backoff delay. |
| `NxHub:Sessions:ReconnectMaxSeconds` | 30 | Backoff cap. |
| `NxHub:Sessions:MaxHoldSeconds` | 30 | Default hold watchdog for API holds. |
| `NxHub:Sessions:NeutraliseOnConnect` | `OnlyIfHeld` | `OnlyIfHeld`, `Always` or `Never`. |

## Resource use

Measured in a Debian Bookworm arm64 container on an Apple Silicon host (not yet on a Pi):
about 77–90 MB RSS idle with one session, using workstation non-concurrent GC and invariant
globalisation. The self-contained install is about 119 MB on disk (48 MB compressed).
