# API guide

- Interactive reference: `http://<nxhub>:8080/docs` (Scalar, served locally)
- OpenAPI 3.1 document: `http://<nxhub>:8080/openapi/v1.json` (snapshot in [openapi.json](openapi.json))

## Authentication

Every `/api/*` route except `/api/health` and `/api/info` needs:

```http
Authorization: Bearer <admin token>
```

The token is generated on first start into `<data dir>/admin-token` (on a Pi:
`sudo cat /var/lib/nxhub/admin-token`). You can also set a fixed one with `NxHub__Auth__Token`.

## Conventions

- JSON, camelCase. Enums are camelCase strings.
- Times are ISO 8601 UTC.
- Durations are integer milliseconds (`durationMs`, `maxHoldMs`).
- Errors are [RFC 9457 problem details](https://www.rfc-editor.org/rfc/rfc9457) with a stable `code`:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Switch not connected",
  "status": 409,
  "detail": "'OLED' is not connected. Connect it first.",
  "code": "switch_not_connected",
  "instance": "/api/switches/sw_k3f9x2m7q1ab/controller/press",
  "traceId": "00-…"
}
```

| HTTP | `code` | Meaning |
| --- | --- | --- |
| 400 | `validation_failed` | Field errors in `errors` (for example `{"host": ["…"]}`). |
| 400 | `invalid_request` | Malformed JSON or an unknown enum value (the detail lists valid buttons). |
| 401 | `unauthorized` | Missing or wrong token. |
| 404 | `switch_not_found`, `no_diagnostics`, `not_found` | |
| 409 | `switch_not_connected` | Connect first. |
| 409 | `diagnostics_in_progress` | One run per Switch at a time. |
| 409 | `macro_running`, `macro_not_running` | One macro per Switch at a time. |
| 404 | `macro_not_found` | |
| 422 | `screenshot_unavailable` | The console returned no image (see Live view). |
| 403 | `demo_read_only` | Public demo instance: settings and saving are switched off. |
| 502 | `switch_connection_lost`, `switch_protocol_error` | The Switch dropped or answered oddly. NX Hub reconnects automatically. |
| 504 | `switch_timeout` | The Switch did not answer in time. NX Hub reconnects automatically. |
| 500 | `internal_error` | A bug. Check the logs (use `traceId`). |

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/health` | Liveness (no auth). |
| GET | `/api/info` | Version and whether auth is required (no auth). |
| GET | `/api/switches` | List Switches with live status. |
| POST | `/api/switches` | Add `{name, host, port?, enabled?}`. Returns 201. |
| GET/PUT/DELETE | `/api/switches/{id}` | Read, replace or remove. |
| GET | `/api/switches/{id}/status` | Connection state, latency, capabilities, held inputs. |
| POST | `/api/switches/{id}/connect` | Start (and keep) connecting. Waits up to 10 s for the attempt. |
| POST | `/api/switches/{id}/disconnect` | Release held inputs, disconnect, stop auto-connect. |
| GET | `/api/switches/{id}/activity?limit=50` | Human-readable recent activity. |
| GET | `/api/switches/{id}/controller` | Held buttons and stick positions. |
| POST | `/api/switches/{id}/controller/press` | `{button, durationMs?}`: press then release. Returns after the release. |
| POST | `/api/switches/{id}/controller/hold` | `{button, maxHoldMs?}`: hold until released or the watchdog fires. |
| POST | `/api/switches/{id}/controller/release` | `{button}` |
| POST | `/api/switches/{id}/controller/stick` | `{stick, x, y, durationMs?, maxHoldMs?}` with x and y in −1…1 (up is +y). |
| POST | `/api/switches/{id}/controller/release-all` | `{everything?: true}` releases all buttons and centres both sticks. |
| POST | `/api/switches/{id}/controller/reset` | Release everything, then detach the virtual controller. |
| POST | `/api/switches/{id}/diagnostics` | `{includeControllerTest?: false}`: run the system check. |
| GET | `/api/switches/{id}/diagnostics/latest` | Most recent report since start. |
| GET | `/api/switches/{id}/screen?maxAgeMs=500` | Current screen as JPEG (live view). |
| GET/POST | `/api/macros` | List / save macros. |
| GET/PUT/DELETE | `/api/macros/{macroId}` | Read, replace or delete a macro. |
| GET | `/api/switches/{id}/macro` | Current or last macro run on this Switch. |
| POST | `/api/switches/{id}/macro/run` | `{macroId}` (saved) or `{macro: {...}}` (one-off). Returns 202. |
| POST | `/api/switches/{id}/macro/pause` · `/resume` · `/stop` | Control the running macro. |
| GET (WS) | `/api/events` | Live events (below). |

Buttons: `a b x y l r zl zr plus minus dpadUp dpadDown dpadLeft dpadRight home capture leftStick rightStick`.
Sticks: `left right`.

## Examples

### curl

```bash
TOKEN=$(sudo cat /var/lib/nxhub/admin-token)
HUB=http://nxhub.local:8080
SW=$(curl -s -H "Authorization: Bearer $TOKEN" $HUB/api/switches | jq -r '.[0].id')

# Press A for 100 ms
curl -s -X POST "$HUB/api/switches/$SW/controller/press" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"button":"a","durationMs":100}'

# Push the left stick up for half a second
curl -s -X POST "$HUB/api/switches/$SW/controller/stick" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"stick":"left","x":0,"y":1,"durationMs":500}'

# System check
curl -s -X POST "$HUB/api/switches/$SW/diagnostics" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{}' | jq '.overall'
```

### Python (no SDK yet; `requests` is enough)

```python
import requests

HUB = "http://nxhub.local:8080"
s = requests.Session()
s.headers["Authorization"] = "Bearer " + open("admin-token").read().strip()

switch_id = s.get(f"{HUB}/api/switches").json()[0]["id"]
for button in ["a", "dpadDown", "a"]:
    r = s.post(f"{HUB}/api/switches/{switch_id}/controller/press", json={"button": button, "durationMs": 100})
    r.raise_for_status()
```

### iOS Shortcuts

Use **Get Contents of URL**: method `POST`, URL
`http://<pi-ip>:8080/api/switches/<id>/controller/press`, headers `Authorization: Bearer <token>`
and `Content-Type: application/json`, request body JSON `button` = `home`.

## Macros

A macro is a list of steps plus how many times to run the whole thing (`repeat`, 0 = until stopped):

```json
{
  "name": "Skip intro",
  "repeat": 1,
  "steps": [
    { "type": "press", "button": "a", "durationMs": 100 },
    { "type": "wait", "durationMs": 1500 },
    { "type": "repeat", "count": 3, "steps": [
      { "type": "press", "button": "dpadDown", "durationMs": 80 },
      { "type": "wait", "durationMs": 300 }
    ] },
    { "type": "stick", "stick": "left", "x": 0, "y": 1, "durationMs": 500 },
    { "type": "hold", "button": "zr" },
    { "type": "wait", "durationMs": 2000 },
    { "type": "release", "button": "zr" }
  ]
}
```

| Step | Fields |
| --- | --- |
| `press` | `button`, `durationMs` (20–10000, default 100) |
| `hold` / `release` | `button` (holds auto-release after 5 minutes at most) |
| `stick` | `stick`, `x`, `y` (−1…1), optional `durationMs` (recentres afterwards) |
| `wait` | `durationMs` (1 ms – 1 hour) |
| `repeat` | `count` (1–100000), `steps` (nesting up to 3 deep) |

Limits: 1000 steps in total. One macro runs per Switch at a time; it goes through the same safety
rules as manual input, and **anything it still holds is released when it ends**, however it ends.
Validation errors name the exact step, for example `steps[2].durationMs`.

```bash
curl -X POST "$HUB/api/switches/$SW/macro/run" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"macroId":"mc_k3f9x2m7q1ab"}'
```

## Live view

`GET /api/switches/{id}/screen` returns a JPEG of the current screen, captured with sys-botbase's
`pixelPeek`. Frames up to `maxAgeMs` old (default 500) are shared between viewers, so several viewers
cost the Switch one capture at a time. Response headers: `X-Frame-Sequence`, `X-Captured-At`,
`X-Capture-Ms`. Capturing blocks sys-botbase briefly, so button input can lag a little while live view
is on. Some consoles return no image (upstream notes the debug-unit flag); you get
`422 screenshot_unavailable` and everything else keeps working.

## Events

Connect a WebSocket to `/api/events` (optionally `?switchId=sw_…`). When auth is on, the
**first message you send** must be:

```json
{"type": "auth", "token": "<admin token>"}
```

within 5 seconds, otherwise the socket closes with code 1008. The server then sends:

```json
{"type": "hello", "server": "NX Hub", "version": "0.1.0"}
```

then one `switch.state` snapshot per Switch, then live events:

| `type` | `data` |
| --- | --- |
| `switch.state` | Full status (same shape as `GET /status`). Sent on every connection state change. |
| `controller.state` | `{heldButtons, sticks, pendingNeutralise}` when held inputs change. |
| `activity` | `{sequence, timestamp, level, category, message}` |
| `diagnostics.completed` | The report. |
| `macro.state` | The run (`status`, `iteration`, `stepsExecuted`, `currentStep`, `error`…). |
| `macro.saved` / `macro.deleted` | A macro changed. |
| `switch.added` / `switch.updated` / `switch.removed` | Device configuration (or `null` on removal). |

Every event has `{type, switchId, timestamp, data}`. Ignore types you do not recognise; more
will be added.
