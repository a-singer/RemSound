# RemSound UDP Relay — server bundle

This folder is a self-contained bundle for running the RemSound UDP relay on a Raspberry Pi (or any systemd-based Linux). Drop it on the Pi, run `install.sh`, open one router port, and you have a relay another RemSound peer can dial.

## Why this exists

RemSound is a Windows audio app that moves real-time audio between two PCs over plain UDP. The standard way two RemSound peers reach each other when one of them is behind a router that won't forward inbound UDP is to run Tailscale on both ends — Tailscale's WireGuard tunnel handles the NAT traversal. That works, but it adds encryption overhead and sometimes routes traffic through Tailscale's DERP relay rather than directly.

A relay is a tiny UDP reflector that sits on a publicly-reachable host. Both peers dial it, the relay learns each peer's apparent address from their first packet, and forwards datagrams between matched peers. RemSound peers stay behind their NATs — the relay is the only thing that needs to be reachable from the public internet.

Whether a relay improves perceived latency depends on geography. Running a relay close to one of the peers can give a measurable improvement over Tailscale's DERP fallback; running a relay far from both peers usually makes things worse than direct Tailscale. The relay is most useful when at least one peer is behind a router that won't let inbound UDP through at all (so direct peer-to-peer is impossible) and Tailscale is undesirable for some reason (encryption overhead, account requirement, mobile data plan).

## What's in this bundle

```
remsound-relay.py        — the relay service itself (Python 3, stdlib only)
remsound-relay.service   — systemd unit
install.sh               — one-shot installer (copy + enable + start)
uninstall.sh             — clean uninstaller
smoke-test.sh            — health check after install
README.md                — this file
```

No external dependencies. The script uses only the Python 3 standard library.

## What it does

- Listens on UDP port **47830**.
- Validates the 12-byte RemSound packet header (magic `RMND`, version 1) and silently drops anything else. **Never decodes audio. Never logs payload bytes.**
- Holds two peer slots. The first two distinct UDP endpoints to send valid RemSound packets claim the slots. Once both slots are filled, every valid packet from one slot is forwarded to the other.
- Slots that go silent for more than 60 seconds are replaced when a fresh endpoint arrives — so a stale pairing self-heals when one side restarts RemSound.
- Logs to `/var/log/remsound-relay.log` — startup, peer-joined / peer-paired / peer-dropped events, per-minute throughput stats. No payload bytes, ever.

## Install

1. Copy this whole folder onto the Pi. SSH or USB stick are both fine. Example over SSH from your laptop, replacing `pi@your-pi-host` with your actual Pi user@host:

   ```
   scp -r server pi@your-pi-host:/home/pi/remsound-relay
   ```

2. SSH into the Pi:

   ```
   ssh pi@your-pi-host
   ```

3. Run the installer:

   ```
   cd /home/pi/remsound-relay
   sudo ./install.sh
   ```

   It copies the script to `/usr/local/sbin/`, installs the systemd unit, enables and starts the service, and prints the status + last few log lines.

4. Optional: run the smoke test to confirm everything works locally:

   ```
   sudo ./smoke-test.sh
   ```

   You should see `[ok] remsound-relay.service is active`, `[ok] listening on UDP 47830`, and `[ok] relay logged a peer_joined event for 127.0.0.1 — header validation works`.

## Open one router port

Without this step the relay only works on your LAN. To make it reachable from the internet:

1. Find the Pi's LAN IP (`hostname -I` on the Pi, take the first IPv4 address).
2. In your router's admin UI, add a port-forward rule:
   - **Protocol:** UDP
   - **WAN port:** 47830
   - **Forward IP:** the Pi's LAN IP (from step 1)
   - **Forward port:** 47830
3. Save / apply.

That's the same kind of rule any other "let an outside service reach a device on my LAN" flow needs. Different routers have it under different menus (Settings → NAT, Settings → Port Forwarding, Advanced → Virtual Server, etc.). The protocol must be **UDP**, not TCP.

If your router doesn't allow inbound port-forwards at all, this relay can't be reached from outside your LAN. In that case stay on Tailscale.

## Tell the other peer the address

The other end of RemSound needs to dial **your-public-host:47830**.

If you have a static public IP, your peer types it directly:

```
123.45.67.89:47830
```

If your IP is dynamic (most home connections are), you'll want a Dynamic-DNS hostname. Free options that work well with consumer routers:

- **Namecheap Dynamic DNS** (if you own a domain there) — set up a host record and run their updater on the Pi or in your router.
- **DuckDNS** (free, no domain needed) — your hostname looks like `something.duckdns.org`.
- **No-IP**, **DynDNS**, etc.

Whichever you pick, the result is a hostname like `mypi.example.com` that always points at your current public IP. The other peer dials `mypi.example.com:47830` and it just works.

## Use it from RemSound

In RemSound's "Add peer by IP or hostname" field, both ends type **just the hostname** — no port suffix needed:

```
your-public-host
```

RemSound defaults to port **47830** (the relay convention) when you don't type a `:port` suffix. Both ends must type the same address so they meet at the same relay.

If you do want LAN peer-to-peer (no relay), type the host with an explicit port:

```
192.168.1.42:47820
```

The build auto-detects relay-vs-LAN from the port: anything ≠ 47820 is treated as a relay endpoint and heartbeat shares the audio sender's UDP socket so both flows traverse the same NAT pinhole. No UI toggle needed.

## Verify it's working

Once both ends have dialled the relay, on the Pi you'll see in `/var/log/remsound-relay.log`:

```
event=peer_joined addr=A.B.C.D:NNNN slots_filled=1
event=peer_joined addr=W.X.Y.Z:NNNN slots_filled=2
event=peer_paired a=A.B.C.D:NNNN b=W.X.Y.Z:NNNN
event=stats forwarded=NNNNN dropped_unpaired=0 rejected_bad_header=0 ...
```

A live tail from the Pi:

```
sudo tail -f /var/log/remsound-relay.log
```

Stats lines come once a minute. `forwarded=N` is the count of packets the relay reflected between peers in that minute. RemSound at 10 ms Opus generates ~100 packets/sec per direction = ~12,000/minute total. PCM with Tight ASIO can be 10× that.

## Operational quick-reference

```
# Service control
sudo systemctl status remsound-relay
sudo systemctl restart remsound-relay
sudo systemctl stop remsound-relay
sudo systemctl start remsound-relay

# Logs
sudo tail -f /var/log/remsound-relay.log
sudo journalctl -u remsound-relay --since "30 minutes ago" --no-pager

# Listening socket
sudo ss -lunp | grep 47830

# Health check (re-runnable any time)
sudo /home/pi/remsound-relay/smoke-test.sh
```

## Update / re-install

To pick up a newer copy of the bundle, `git pull` (or re-download) the RemSound repo, copy the latest `server/` folder to the Pi, and run `sudo ./install.sh` again. It overwrites the script and restarts the service. No state to migrate.

## Uninstall

```
sudo ./uninstall.sh
```

Stops the service, disables it, removes the script and systemd unit. Leaves the log file alone — delete `/var/log/remsound-relay.log` yourself if you don't want it kept. Doesn't touch your router port-forward.

## Troubleshooting

**Service won't start.** `sudo systemctl status remsound-relay` will show an error. The most likely cause is something else already bound to UDP 47830 — `sudo ss -lunp | grep 47830` will show what. Pick a different port: edit `DEFAULT_PORT` in `/usr/local/sbin/remsound-relay.py`, restart with `sudo systemctl restart remsound-relay`, and tell the other peer the new port number.

**Relay running but the other peer can't connect.** Almost always the router port-forward. Check:

- The rule is **UDP** not TCP.
- The internal IP matches the Pi's actual LAN IP (run `hostname -I` to confirm).
- WAN port and forward port are both 47830.
- Some routers need a reboot after a new rule. Try one if nothing else helps.

To prove the port is open from outside, on a non-LAN machine (a phone on mobile data works) send a test packet:

```
python3 -c "import socket; s=socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.sendto(bytes([0x52,0x4D,0x4E,0x44,1,4,1,0,1,0,0,0]), ('your-public-host', 47830))"
```

Then on the Pi:

```
sudo tail -10 /var/log/remsound-relay.log
```

You should see `event=peer_joined` with the source address.

**Audio plays but is laggy / clicky.** That's not the relay's problem. The relay forwards packets blind — anything fancy belongs at the RemSound endpoints. Check the RemSound diagnostics: codec choice (PCM vs Opus, Opus 10 ms is the lowest-latency Opus), buffer smoothness, and either side's upload bandwidth. PCM with Tight ASIO is bandwidth-heavy (often 10+ Mbps); Opus 10 ms is around 200 kbps.

**Asymmetric packet rate in the stats line.** Like `peers=[X(rx=90000,tx=12000), Y(rx=12000,tx=90000)]`. That just means one side is sending many more packets than the other — usually because one side is on PCM-with-Tight-ASIO and the other is on Opus. Fine in itself, but if the high-rate side's home upload is saturated it'll cause jitter. Switch that side to Opus 10 ms.

## Wire format reference (for anyone reading the relay code)

Header is 12 bytes, little-endian:

```
uint32 magic    'RMND' (0x444E4D52)
uint8  version  1
uint8  type     1=Format 2=Audio 3=KeepAlive 4=Heartbeat
uint16 streamId
uint32 sequence
```

The relay validates magic and version, reads the type for logging, and **does not interpret anything past byte 6**. Everything after the header is opaque payload.

## Security note

The relay has no authentication and the audio between peers is plaintext UDP. The trade-off is explicit: this is a music-collaboration tool, not a confidential channel. If you really need privacy, the right answer is to run Tailscale on both ends — that gives you WireGuard encryption end-to-end, at the cost of the latency overhead a relay aims to avoid.

## Questions and issues

Open an issue at <https://github.com/Ednunp/RemSound/issues>.
