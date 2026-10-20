#!/usr/bin/env bash
# Installs or upgrades NX Hub from an extracted release folder on a Raspberry Pi (or any systemd Linux).
#
#   tar -xzf nxhub-<version>-linux-arm64.tar.gz
#   cd nxhub-<version>-linux-arm64
#   sudo ./install.sh                 # listens on port 8080
#   sudo ./install.sh --port 8810     # pick another port (written as a systemd drop-in)
#
# What it does (nothing else):
#   - copies the application to /opt/nxhub
#   - installs /etc/systemd/system/nxhub.service and starts it
# Data (database, admin token) lives in /var/lib/nxhub and is preserved across upgrades.
# It does not download anything, open firewall ports or change other system settings.
set -euo pipefail

APP_DIR=/opt/nxhub
UNIT=/etc/systemd/system/nxhub.service
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DROPIN_DIR=/etc/systemd/system/nxhub.service.d
PORT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --port)
      PORT="${2:-}"
      shift 2
      ;;
    -h|--help)
      sed -n '2,12p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 1
      ;;
  esac
done

if [[ -n "$PORT" ]] && ! [[ "$PORT" =~ ^[0-9]+$ && "$PORT" -ge 1024 && "$PORT" -le 65535 ]]; then
  echo "--port must be a number between 1024 and 65535." >&2
  exit 1
fi

if [[ $EUID -ne 0 ]]; then
  echo "Run with sudo: sudo ./install.sh" >&2
  exit 1
fi

if [[ ! -x "$HERE/app/nxhub" ]]; then
  echo "Cannot find app/nxhub next to this script. Run it from the extracted release folder." >&2
  exit 1
fi

arch="$(uname -m)"
case "$arch" in
  aarch64|arm64) expected=arm64 ;;
  x86_64) expected=x64 ;;
  *) echo "Unsupported architecture: $arch (NX Hub needs 64-bit ARM or x64)." >&2; exit 1 ;;
esac
if [[ -f "$HERE/TARGET" ]] && ! grep -q "linux-$expected" "$HERE/TARGET"; then
  echo "This package is for $(cat "$HERE/TARGET") but this machine is linux-$expected." >&2
  exit 1
fi

# .NET needs OpenSSL 3 at runtime (present on Raspberry Pi OS; minimal images may lack it).
if command -v dpkg >/dev/null && ! dpkg -s libssl3 >/dev/null 2>&1 && ! dpkg -s libssl3t64 >/dev/null 2>&1; then
  echo "Missing dependency: libssl3. Install it first:  sudo apt install libssl3" >&2
  exit 1
fi

WRITE_DROPIN=0
if [[ -n "$PORT" ]]; then
  WRITE_DROPIN=1
elif [[ -f "$DROPIN_DIR/10-listen.conf" ]]; then
  # Keep the port chosen at a previous install.
  PORT="$(sed -n 's/.*:\([0-9]*\)$/\1/p' "$DROPIN_DIR/10-listen.conf")"
fi
PORT="${PORT:-8080}"

if systemctl is-active --quiet nxhub; then
  echo "Stopping running NX Hub (held controller inputs are released on shutdown)..."
  systemctl stop nxhub
fi

if command -v ss >/dev/null && ss -ltn | grep -qE "[:.]$PORT[[:space:]]"; then
  echo "Port $PORT is already in use by another program. Re-run with --port <free port>." >&2
  exit 1
fi

echo "Installing to $APP_DIR..."
rm -rf "$APP_DIR.new"
mkdir -p "$APP_DIR.new"
cp -a "$HERE/app/." "$APP_DIR.new/"
chown -R root:root "$APP_DIR.new"
chmod -R go-w "$APP_DIR.new"
rm -rf "$APP_DIR.old"
[[ -d "$APP_DIR" ]] && mv "$APP_DIR" "$APP_DIR.old"
mv "$APP_DIR.new" "$APP_DIR"
rm -rf "$APP_DIR.old"

install -m 0644 "$HERE/nxhub.service" "$UNIT"
if [[ "$WRITE_DROPIN" == 1 ]]; then
  mkdir -p "$DROPIN_DIR"
  printf '[Service]\nEnvironment=Urls=http://*:%s\n' "$PORT" > "$DROPIN_DIR/10-listen.conf"
fi
systemctl daemon-reload
systemctl enable --now nxhub

echo "Waiting for NX Hub to start..."
for _ in $(seq 1 60); do
  # Ready = listening and answering, not just "process started" (matters on upgrades).
  if [[ -f /var/lib/nxhub/admin-token ]] && systemctl is-active --quiet nxhub \
    && { exec 3<>"/dev/tcp/127.0.0.1/$PORT"; } 2>/dev/null; then
    exec 3<&- 3>&-
    break
  fi
  sleep 1
done

if ! systemctl is-active --quiet nxhub; then
  echo "NX Hub did not start. See: journalctl -u nxhub -n 50" >&2
  exit 1
fi

host="$(hostname).local"
echo
echo "NX Hub is running."
echo "  Open:        http://$host:$PORT  (or http://<pi-ip>:$PORT)"
echo "  Admin token: sudo cat /var/lib/nxhub/admin-token"
echo "  Logs:        journalctl -u nxhub -f"
