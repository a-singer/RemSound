#!/bin/bash
# install.sh — RemSound UDP relay installer for a stock Raspberry Pi (or any
# systemd Linux). Idempotent: re-running it just refreshes the files and
# restarts the service.
#
# Run with sudo from inside this folder:
#   sudo ./install.sh
#
# What it does:
#   1. Sanity checks: systemd present, python3 present.
#   2. Copies remsound-relay.py to /usr/local/sbin/.
#   3. Copies remsound-relay.service to /etc/systemd/system/.
#   4. Creates an empty /var/log/remsound-relay.log if missing.
#   5. systemctl daemon-reload, enable + start the service.
#   6. Prints status and the last few log lines so you can see it's alive.

set -euo pipefail

if [[ $EUID -ne 0 ]]; then
  echo "This installer must run as root. Try: sudo ./install.sh" >&2
  exit 1
fi

# Resolve the directory this script lives in, regardless of where it was launched from.
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"

if [[ ! -f "$SCRIPT_DIR/remsound-relay.py" || ! -f "$SCRIPT_DIR/remsound-relay.service" ]]; then
  echo "Cannot find remsound-relay.py and/or remsound-relay.service next to install.sh." >&2
  echo "Run the installer from inside the unzipped bundle folder." >&2
  exit 1
fi

if ! command -v systemctl >/dev/null 2>&1; then
  echo "systemctl not found. This bundle expects a systemd-based Linux (Raspberry Pi OS, Debian, Ubuntu, etc.)." >&2
  exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 not found. Install it first: sudo apt-get install -y python3" >&2
  exit 1
fi

echo "Installing relay script to /usr/local/sbin/remsound-relay.py ..."
install -o root -g root -m 755 "$SCRIPT_DIR/remsound-relay.py" /usr/local/sbin/remsound-relay.py
python3 -m py_compile /usr/local/sbin/remsound-relay.py

echo "Installing systemd unit to /etc/systemd/system/remsound-relay.service ..."
install -o root -g root -m 644 "$SCRIPT_DIR/remsound-relay.service" /etc/systemd/system/remsound-relay.service

echo "Ensuring log file exists ..."
touch /var/log/remsound-relay.log
chown root:root /var/log/remsound-relay.log
chmod 0644 /var/log/remsound-relay.log

echo "Reloading systemd, enabling, and starting remsound-relay ..."
systemctl daemon-reload
systemctl enable remsound-relay.service >/dev/null
systemctl restart remsound-relay.service

# Give the service a beat to come up before we try to read its state.
sleep 1

echo
echo "=== service status ==="
systemctl --no-pager --full status remsound-relay.service || true

echo
echo "=== last 10 log lines ==="
tail -n 10 /var/log/remsound-relay.log 2>/dev/null || echo "(log empty so far)"

echo
echo "=== listening socket check ==="
if command -v ss >/dev/null 2>&1; then
  ss -lunp 2>/dev/null | grep ':47830' || echo "WARNING: no listener on UDP 47830 — see service status above."
else
  echo "(ss not installed, skipping)"
fi

echo
echo "Install complete. The relay is running on UDP 47830."
echo "Next step: open a UDP 47830 port-forward on your router toward this Pi's LAN address,"
echo "then dial the Pi's public hostname:47830 from RemSound on each end."
echo "See README.md for full operational notes."
