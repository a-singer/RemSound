#!/bin/bash
# uninstall.sh — Remove the RemSound UDP relay cleanly.
#
# Run with sudo:
#   sudo ./uninstall.sh
#
# Stops the service, disables it, removes the script and unit file, and
# leaves the log file in place (rename or delete it yourself if you don't
# want it kept). Does NOT touch your router port-forward — that's separate.

set -euo pipefail

if [[ $EUID -ne 0 ]]; then
  echo "This uninstaller must run as root. Try: sudo ./uninstall.sh" >&2
  exit 1
fi

if systemctl list-unit-files remsound-relay.service >/dev/null 2>&1; then
  echo "Stopping remsound-relay ..."
  systemctl stop remsound-relay.service || true
  echo "Disabling remsound-relay ..."
  systemctl disable remsound-relay.service >/dev/null 2>&1 || true
fi

if [[ -f /etc/systemd/system/remsound-relay.service ]]; then
  echo "Removing /etc/systemd/system/remsound-relay.service ..."
  rm -f /etc/systemd/system/remsound-relay.service
fi

if [[ -f /usr/local/sbin/remsound-relay.py ]]; then
  echo "Removing /usr/local/sbin/remsound-relay.py ..."
  rm -f /usr/local/sbin/remsound-relay.py
fi

systemctl daemon-reload

echo
echo "Uninstall complete."
echo "Note: /var/log/remsound-relay.log is left in place — delete it yourself if you don't want it."
echo "Note: any router port-forward you added for UDP 47830 is unchanged — remove that yourself if you no longer need it."
