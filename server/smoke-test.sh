#!/bin/bash
# smoke-test.sh — Quick health check for the RemSound relay.
#
# Run after install. Confirms the service is running, listening on UDP 47830,
# and accepts a valid RemSound header (silently dropping it as unpaired, which
# is the expected behaviour with no real peers connected).
#
# Run as a regular user; only the log read at the end needs sudo and that
# step degrades gracefully if you don't have it.

set -u

GREEN=$'\033[0;32m'
RED=$'\033[0;31m'
YELLOW=$'\033[0;33m'
RESET=$'\033[0m'

ok()   { printf "%s[ok]%s   %s\n"   "$GREEN" "$RESET" "$*"; }
fail() { printf "%s[FAIL]%s %s\n"   "$RED"   "$RESET" "$*"; }
warn() { printf "%s[warn]%s %s\n"   "$YELLOW" "$RESET" "$*"; }

failures=0

# 1. Service active?
if systemctl is-active --quiet remsound-relay.service; then
  ok "remsound-relay.service is active"
else
  fail "remsound-relay.service is not active. Try: sudo systemctl status remsound-relay"
  failures=$((failures + 1))
fi

# 2. Listening on UDP 47830?
if command -v ss >/dev/null 2>&1; then
  if ss -lun 2>/dev/null | grep -q ':47830'; then
    ok "listening on UDP 47830"
  else
    fail "no UDP 47830 listener visible to ss"
    failures=$((failures + 1))
  fi
else
  warn "ss not installed, skipping listener check"
fi

# 3. Send a synthetic valid RemSound header from this machine to localhost,
#    confirm the relay accepts it (it'll be admitted to slot 1 and then
#    dropped as unpaired, which logs an event=peer_joined line).
if command -v python3 >/dev/null 2>&1; then
  python3 - <<'PY'
import socket
# Magic 'RMND' (little-endian 0x444E4D52), version 1, type 4 (Heartbeat),
# stream id 1, sequence 1.
header = bytes([0x52, 0x4D, 0x4E, 0x44, 1, 4, 1, 0, 1, 0, 0, 0])
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.sendto(header, ("127.0.0.1", 47830))
s.close()
PY
  ok "sent synthetic valid RemSound header to 127.0.0.1:47830"
else
  warn "python3 not installed, skipping header send"
fi

# 4. Look for the resulting peer_joined event in the relay log.
sleep 1
if [[ -r /var/log/remsound-relay.log ]]; then
  if tail -20 /var/log/remsound-relay.log 2>/dev/null | grep -q "event=peer_joined.*127.0.0.1"; then
    ok "relay logged a peer_joined event for 127.0.0.1 — header validation works"
  else
    warn "no peer_joined event for 127.0.0.1 found in last 20 log lines (may have aged out if you ran this twice)"
  fi
elif sudo -n test -r /var/log/remsound-relay.log 2>/dev/null; then
  if sudo tail -20 /var/log/remsound-relay.log 2>/dev/null | grep -q "event=peer_joined.*127.0.0.1"; then
    ok "relay logged a peer_joined event for 127.0.0.1 — header validation works"
  else
    warn "no peer_joined event for 127.0.0.1 found in last 20 log lines"
  fi
else
  warn "cannot read /var/log/remsound-relay.log (try: sudo $0 to also read the log)"
fi

echo
if (( failures == 0 )); then
  echo "${GREEN}All checks passed.${RESET}"
  exit 0
else
  echo "${RED}${failures} check(s) failed.${RESET}"
  exit 1
fi
