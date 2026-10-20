# Hardware validation checklist

Nothing in NX Hub has been verified on a physical Switch yet. Work through these stages in
order. Each stage depends on the previous one. Record results in the table at the end. Do not
mark anything compatible in the README until it passes here.

**Record before starting:**

- Switch model (V1 / V2 / Lite / OLED), firmware version, Atmosphère version
- sys-botbase flavour and version (olliz0r 2.x or sys-botbase-cpp 3.x, and its `config.cfg` mode)
- Game used for testing (title and version), or HOME menu
- NX Hub version / commit, Pi model and OS (`cat /etc/os-release`, `uname -m`)
- Network: Pi wired or Wi-Fi; Switch on 2.4 or 5 GHz

## Stage 0: Preconditions

- [ ] Switch boots into Atmosphère CFW. sys-botbase is in `atmosphere/contents/430000000000000B/`.
- [ ] ~20 s after boot the Joy-Con/controller LEDs flash (sys-botbase's "ready" signal).
- [ ] Switch IP noted (System Settings → Internet) and reserved in the router.
- [ ] Switch is **not** connected to any other sys-botbase client (SysBot.NET, PKHeX LiveHeX…).
  sys-botbase-cpp accepts only one client.

## Stage 1: Connect and diagnose (the first milestone)

- [ ] Add the Switch in the UI. It reaches **Connected** within a few seconds.
- [ ] Status shows the expected module and version (for example "sys-botbase 2.5").
      ⚠ Check that the version-to-implementation mapping in [PROTOCOL.md](PROTOCOL.md) is right.
- [ ] Run **System check** without the controller test. Result is **READY** or **DEGRADED** (ping only).
- [ ] Note latency min/avg/max: ________ ms. (Expectation from source reading: about 50–100 ms on 2.x.)
- [ ] "Running title" shows the game's title ID when in a game, and "No game is running" on the
      HOME menu. ⚠ Check the title ID matches the real one (byte order).

## Stage 2: Press A

Start on the HOME menu with a game tile selected.

- [ ] Run the system check **with** the controller test. The Switch shows a new controller
      connecting (Pro Controller). Note whether a "controller connected" pop-up appears: ________
- [ ] Tap **A** in the UI. The Switch responds (opens the selected item).
- [ ] `curl … /controller/press -d '{"button":"b"}'` from a terminal backs out.
- [ ] Tap HOME. Note whether it works (upstream mentions "HOME button eaten" issues): ________

## Stage 3: Full controller

- [ ] Every button: A B X Y L R ZL ZR + − D-pad ×4 HOME CAPTURE L3 R3. Note any that misbehave.
- [ ] Left stick moves the HOME-menu cursor in the expected directions.
      ⚠ Confirm **up is +Y** (if inverted, fix `StickPosition` handling and its tests).
- [ ] Right stick in a game that uses it.
- [ ] Hold A in the UI for about 3 s: the game sees a continuous hold (no flicker).
- [ ] Minimum reliable press: try `durationMs` 20, 33, 50, 100 in a menu. Smallest reliable: ____ ms.
- [ ] Two buttons at once (for example L + R on the "Press L and R" grip screen).

## Stage 4: Safety ("never left held")

- [ ] Hold ZR via the API with `maxHoldMs: 3000`. The Switch releases it after about 3 s.
- [ ] Hold a stick from the UI, then switch apps on the phone. The stick recentres.
- [ ] Hold A, then pull the Pi's network cable for 15 s, then reconnect. NX Hub reconnects and
      the activity log shows "Released inputs left held when the connection dropped: A".
      Note whether A stayed held on the Switch during the outage (expected: yes, that is upstream behaviour): ____
- [ ] `sudo systemctl stop nxhub` while holding a button. The button is released.
- [ ] **Reset controller** in the UI: the controller disconnects from the Switch. Pressing A
      afterwards re-attaches it with nothing stuck.

## Stage 5: Connection resilience

- [ ] Put the Switch to sleep. NX Hub goes to Reconnecting within about 15 s (heartbeat + timeout).
- [ ] Wake the Switch. NX Hub reconnects by itself (note the time to reconnect: ____ s).
- [ ] Reboot the Switch. NX Hub reconnects about 20 s after boot.
- [ ] Restart the Pi. NX Hub starts at boot and reconnects.
- [ ] Leave it connected for 1 hour idle. "Drops" stays at 0 (or note the count: ____).
- [ ] While connected, run SysBot.NET or LiveHeX against the same Switch. Note what happens to each
      client (2.x allows several; cpp should refuse the second): ____

## Stage 6: sys-botbase-cpp (if available)

- [ ] Version shows `3.33`. Capabilities show the tick-precise controller.
- [ ] Stages 1–5 pass with cpp as well.
- [ ] With `configure enableBackwardsCompat 0` set by another tool, NX Hub shows the 3.32 warning.

## Results

| Stage | Pass/Fail | Date | NX Hub commit | Switch/firmware | sys-botbase | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Pass | 2026-10-20 | 5f116fa | Switch at 192.168.8.x, firmware not recorded | sys-botbase 2.41 | Pi 5 (Bookworm, wired). Switch on HOME, Wi-Fi. |
| 1 | Pass | 2026-10-20 | 5f116fa | " | sys-botbase 2.41 | Connected at once. READY. Latency min 47 / avg 49 / max 51 ms (matches the 50 ms post-command sleep). Title check correctly reported HOME. A later auto-sleep was detected and backed off as expected. |
| 2 | Partial | 2026-10-20 | 21f88f0 | " | sys-botbase 2.41 | First real input: `press dpadRight 100 ms` via the API moved the HOME-menu cursor one tile right (owner confirmed). Press and release both sent, nothing left held. HOME held 1.5 s then B 5 s later: owner saw an overlay open and close on the HOME menu (consistent with Quick Settings opening on HOME-hold and closing on B; observed casually). Still to do: A, a short HOME tap from inside a game, and the controller-connected pop-up check. |
| 3 | | | | | | |
| 4 | | | | | | |
| 5 | | | | | | |
| 6 | | | | | | |

When a stage passes, update `README.md` ("Status") and `HANDOVER.md`, and remove any ⚠ in
PROTOCOL.md that the test confirmed.
