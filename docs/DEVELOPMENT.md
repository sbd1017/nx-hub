# Development

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (`global.json` pins 10.0.x, latest feature band)
- Node.js 22+ and npm (for the web UI)
- Optional: Docker (for the ARM64 smoke test)

No Switch is needed: the repository includes a sys-botbase simulator.

## Quick start (no hardware)

```bash
# 1. Build the web UI into the server's wwwroot
cd web && npm install && npm run build && cd ..

# 2. Terminal A: simulator on localhost:6000 (pretends to be sys-botbase 2.5)
dotnet run --project src/NxHub.Simulator -- --port 6000
#    or the sys-botbase-cpp flavour:  -- --port 6000 --flavour cpp

# 3. Terminal B: NX Hub on http://localhost:8080
dotnet run --project src/NxHub.Server
```

Open http://localhost:8080. The token gate asks for the admin token, which is in
`src/NxHub.Server/data/admin-token` (the default data directory in development). Add a Switch
with host `127.0.0.1`, port `6000`.

**Demo mode** (what https://mubar.uk/nx-hub/ runs): no separate simulator needed.

```bash
NxHub__Demo__Enabled=true dotnet run --project src/NxHub.Server -- --urls http://localhost:8091
```

The simulator's `--interactive-screen` flag gives the same pretend menu when you run it standalone.

For UI work with hot reload, run `npm run dev` in `web/` and open http://localhost:5173. Vite
proxies `/api` (including the WebSocket) to the server on :8080.

## Tests

```bash
dotnet test NxHub.slnx
```

| Project | What it covers | Hardware? |
| --- | --- | --- |
| `NxHub.Protocol.Tests` | Command encoding and injection guards, response parsing, capability inference, client handshake/timeouts/poisoning/serialisation, TCP framing (split packets, CRLF, overlong lines, peer close) against real loopback sockets and the simulator | No |
| `NxHub.Core.Tests` | Session state machine, exponential backoff (fake clock), heartbeat loss detection, reconnect, address changes, disconnect, controller press/hold/release, cancellation still releases, watchdogs, neutralise-on-reconnect, diagnostics paths, validation, event hub, manager CRUD | No (in-memory `FakeSwitch`) |
| `NxHub.Server.Tests` | Full HTTP pipeline via `WebApplicationFactory` against the TCP simulator: auth, CRUD, persistence across restarts, controller round trips, error model, reconnect after drops, diagnostics, OpenAPI, WebSocket auth and events, token file permissions | No |

Time-dependent logic uses `TimeProvider`. Core tests use `FakeTimeProvider` to advance time
deterministically instead of sleeping.

**What tests cannot tell you:** whether a real Switch reacts to the inputs, real latency, or
behaviour of specific sys-botbase builds. That is what [HARDWARE-VALIDATION.md](HARDWARE-VALIDATION.md) is for.

## Code quality gates

```bash
dotnet build NxHub.slnx -c Release          # warnings are errors, analyzers on
dotnet format NxHub.slnx --verify-no-changes
cd web && npm run format:check && npm run build   # prettier, then tsc --noEmit + vite build
```

CI (`.github/workflows/ci.yml`) runs all of the above. It also cross-publishes for linux-arm64,
linux-x64, osx-arm64 and win-x64, and smoke-tests the arm64 package inside `debian:bookworm` on
an ARM runner.

## Packaging

```bash
scripts/package.sh            # → artifacts/nxhub-<version>-linux-arm64.tar.gz (+ .sha256)
scripts/package.sh linux-x64
```

The package contains `app/` (self-contained), `install.sh`, `nxhub.service`, `LICENSE`,
`NOTICE` and `TARGET`. See [RASPBERRY-PI.md](RASPBERRY-PI.md).

## Updating the OpenAPI snapshot

```bash
curl -s http://localhost:8080/openapi/v1.json | python3 -m json.tool > docs/openapi.json
```

## Repository layout

```text
src/NxHub.Protocol     sys-botbase wire protocol (commands, client, transports)
src/NxHub.Core         sessions, controller, diagnostics, events (no ASP.NET)
src/NxHub.Server       ASP.NET Core host, SQLite, auth, API, static UI
src/NxHub.Simulator    sys-botbase simulator (dev/test only)
web/                   React + TypeScript UI → src/NxHub.Server/wwwroot
tests/                 xUnit tests (+ tests/Shared test doubles)
deploy/                systemd unit and install script
scripts/               packaging
docs/                  architecture, protocol, API, licensing, Pi, hardware checklist
```

## Conventions

- Raw sys-botbase strings only in `NxHub.Protocol/SysBotBase/SysBotCommand.cs`.
- Core must not reference ASP.NET or any game-specific library.
- New dependencies: permissive licences only in Core/Protocol/Server. Record them in `THIRD-PARTY.md`.
- Never commit IP addresses, tokens or `data/` directories.
- Do not claim hardware behaviour in docs or PRs unless it was tested on hardware. Say what was tested and how.
