# Raspberry Pi 5 setup

Reference platform: **Raspberry Pi 5, Raspberry Pi OS / Debian 12 "Bookworm", 64-bit (arm64)**.

> **Status:** installed with `install.sh --port 8810` on a physical **Raspberry Pi 5 (8 GB,
> Debian 12 Bookworm, NVMe)** on 2026-10-20. The service started under the hardened unit, used
> about 82 MB RSS, and served the API on the LAN alongside other services. Control of a physical
> Switch is **not yet verified**: see the [hardware checklist](HARDWARE-VALIDATION.md).

## Requirements

- 64-bit OS (`uname -m` prints `aarch64`). 32-bit Raspberry Pi OS is not supported.
- `libssl3` (preinstalled on Raspberry Pi OS; on minimal Debian run `sudo apt install libssl3`).
- The Pi and the Switch on the same LAN. Wired Ethernet on the Pi is recommended.
- About 150 MB disk and about 100 MB RAM.

No .NET installation is needed: the package is self-contained.

## Install

On your computer, build the package (or download a release once releases exist):

```bash
scripts/package.sh linux-arm64
scp artifacts/nxhub-*-linux-arm64.tar.gz pi@raspberrypi.local:
```

On the Pi:

```bash
tar -xzf nxhub-*-linux-arm64.tar.gz
cd nxhub-*-linux-arm64
sudo ./install.sh
```

The installer copies the app to `/opt/nxhub`, installs and starts `nxhub.service`, and prints the
URL. If port 8080 is already used on your Pi, pick another with `sudo ./install.sh --port 8810`.
The port is stored as a systemd drop-in (`/etc/systemd/system/nxhub.service.d/10-listen.conf`)
and kept on upgrades. The installer refuses to continue if the port is taken. It downloads nothing and changes nothing else on the system. Read it first: it is short.

Then:

1. Get the token: `sudo cat /var/lib/nxhub/admin-token`
2. Open `http://<pi-hostname>.local:8080` (or `http://<pi-ip>:8080`) on your phone or computer.
3. Paste the token, then add your Switch (its IP is under **System Settings → Internet** on the Switch).

On iPhone, use **Share → Add to Home Screen** for an app-like experience.

## Upgrade

Repeat the install steps with the new package. Configuration and the token in `/var/lib/nxhub`
are kept. The service is stopped first, which releases any held controller inputs.

## Operate

```bash
systemctl status nxhub
journalctl -u nxhub -f                 # structured JSON logs
sudo systemctl restart nxhub
```

To pretty-print logs: `journalctl -u nxhub -o cat | jq -r '"\(.Timestamp) \(.LogLevel) \(.Message)"'`

Change settings with a drop-in instead of editing the unit:

```bash
sudo systemctl edit nxhub
# [Service]
# Environment=Urls=http://0.0.0.0:80
# Environment=NxHub__Sessions__HeartbeatSeconds=5
```

Binding to port 80 additionally needs `AmbientCapabilities=CAP_NET_BIND_SERVICE` and a
non-empty `CapabilityBoundingSet=CAP_NET_BIND_SERVICE` in that drop-in. Keeping 8080 is simpler.

## Uninstall

```bash
sudo systemctl disable --now nxhub
sudo rm -rf /opt/nxhub /etc/systemd/system/nxhub.service
sudo systemctl daemon-reload
sudo rm -rf /var/lib/private/nxhub /var/lib/nxhub   # deletes configuration and token
```

## Network and security notes

- NX Hub listens on port 8080 on all interfaces so phones can reach it. **Do not port-forward it.**
  For remote access use a VPN (Tailscale/WireGuard).
- sys-botbase on the Switch (port 6000) has no authentication. Keep the Switch on a trusted
  network.
- The service runs as an ephemeral unprivileged user (`DynamicUser=yes`) with a read-only view of
  the system and no capabilities. Its data directory is `0700`.
- Give the Switch a DHCP reservation in your router so its IP does not change.

## Behind nginx at a sub-path

To reach NX Hub as `https://your-site/nxhub/` through an existing nginx (for example one
fronted by Cloudflare Tunnel), see `deploy/nginx/nxhub-subpath.example.conf` and set
`NxHub__PathBase=/nxhub` in a drop-in (`sudo systemctl edit nxhub`). If nginx already
authenticates you, also set `NxHub__Auth__TrustedProxyUserHeader` to the identity header nginx
overwrites on every request. NX Hub then skips its own token prompt for those requests only.

## `nxhub.local` (future)

The Pi already answers to `<hostname>.local` via Avahi on Raspberry Pi OS. Renaming the Pi to
`nxhub` (`sudo raspi-config` → System → Hostname) gives `http://nxhub.local:8080`. A dedicated
mDNS service advertisement and a one-line installer are planned. The installer will only ship
once releases are signed and checksummed, because `curl | bash` without verification is not
acceptable.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Service fails: "No usable version of libssl" | `sudo apt install libssl3` |
| UI loads but says "Cannot reach NX Hub" | Is the service running? `systemctl status nxhub` |
| Switch stays "Reconnecting", error ConnectionRefused | sys-botbase not installed or not started yet (it starts ~20 s after the Switch boots). |
| Error "TimedOut" / "HostUnreachable" | Wrong IP, Switch asleep, or on a different network/VLAN. Run the system check. |
| Frequent drops | Switch Wi-Fi power saving, weak signal, 2.4 GHz congestion. Prefer 5 GHz. Check the "Drops" counter and diagnostics. |
| Ping check warns but TCP passes | Fine. Many networks block ICMP. |
