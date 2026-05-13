#!/usr/bin/env python3
"""
RemSound UDP relay.

Listens on a single UDP port and reflects RemSound packets between up to two
peer endpoints. Validates the 12-byte RemSound header (magic 'RMND', version 1)
and silently drops anything else. Never decodes audio.

Pairing model: pragmatic v1. The relay does not key on stream id (two RemSound
instances will use different per-sender stream ids, so keying on it would
prevent pairing). Instead the first two distinct UDP endpoints to send a valid
RemSound packet claim the two peer slots; subsequent valid packets from a
slot's endpoint are reflected to the other slot. Slots that go silent for more
than IDLE_TIMEOUT_SECONDS are replaced when a fresh endpoint arrives.

Spec and operational notes: see README.md alongside this script.
"""

from __future__ import annotations

import argparse
import logging
import logging.handlers
import select
import signal
import socket
import struct
import sys
import time
from dataclasses import dataclass
from typing import Optional

LISTEN_HOST = "0.0.0.0"
DEFAULT_PORT = 47830
RECV_BUFFER_BYTES = 2048
IDLE_TIMEOUT_SECONDS = 60
STATS_INTERVAL_SECONDS = 60
SOCKET_POLL_TIMEOUT_SECONDS = 1.0
DEFAULT_LOG_PATH = "/var/log/remsound-relay.log"

# Wire format constants — little-endian, see RemSound.Core.RemPacket.
HEADER_LEN = 12
MAGIC = b"RMND"
VERSION = 1
TYPE_FORMAT = 1
TYPE_AUDIO = 2
TYPE_KEEPALIVE = 3
TYPE_HEARTBEAT = 4
PACKET_TYPE_NAMES = {
    TYPE_FORMAT: "Format",
    TYPE_AUDIO: "Audio",
    TYPE_KEEPALIVE: "KeepAlive",
    TYPE_HEARTBEAT: "Heartbeat",
}


@dataclass
class PeerSlot:
    addr: tuple[str, int]
    last_seen: float
    rx_packets: int = 0
    tx_packets: int = 0


@dataclass
class RelayStats:
    forwarded: int = 0
    dropped_unpaired: int = 0
    rejected_bad_header: int = 0
    pair_changes: int = 0


def setup_logger(log_path: str) -> logging.Logger:
    logger = logging.getLogger("remsound-relay")
    logger.setLevel(logging.INFO)
    fmt = logging.Formatter(
        fmt="%(asctime)s level=%(levelname)s %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )
    try:
        fh = logging.handlers.WatchedFileHandler(log_path, encoding="utf-8")
        fh.setFormatter(fmt)
        logger.addHandler(fh)
    except OSError as e:
        sys.stderr.write(f"remsound-relay: could not open {log_path}: {e}\n")
    sh = logging.StreamHandler(sys.stderr)
    sh.setFormatter(fmt)
    logger.addHandler(sh)
    return logger


def parse_header(data: bytes) -> Optional[tuple[int, int, int, int]]:
    """Validate the RemSound header. Returns (version, type, stream_id, sequence) or None."""
    if len(data) < HEADER_LEN:
        return None
    if data[0:4] != MAGIC:
        return None
    version = data[4]
    if version != VERSION:
        return None
    pkt_type = data[5]
    stream_id = struct.unpack_from("<H", data, 6)[0]
    sequence = struct.unpack_from("<I", data, 8)[0]
    return version, pkt_type, stream_id, sequence


class Relay:
    def __init__(self, sock: socket.socket, log: logging.Logger):
        self.sock = sock
        self.log = log
        self.peers: list[PeerSlot] = []
        self.stats = RelayStats()
        self.last_stats_log = time.monotonic()

    @staticmethod
    def _fmt_addr(addr: tuple[str, int]) -> str:
        return f"{addr[0]}:{addr[1]}"

    def find_slot(self, addr: tuple[str, int]) -> Optional[int]:
        for i, p in enumerate(self.peers):
            if p.addr == addr:
                return i
        return None

    def expire_idle(self, now: float) -> None:
        if not self.peers:
            return
        kept: list[PeerSlot] = []
        dropped: list[tuple[str, int]] = []
        for p in self.peers:
            if (now - p.last_seen) <= IDLE_TIMEOUT_SECONDS:
                kept.append(p)
            else:
                dropped.append(p.addr)
        if dropped:
            self.peers = kept
            for addr in dropped:
                self.log.info(
                    "event=peer_dropped reason=idle addr=%s remaining=%d",
                    self._fmt_addr(addr),
                    len(self.peers),
                )
                self.stats.pair_changes += 1

    def admit_or_replace(self, addr: tuple[str, int], now: float) -> int:
        if len(self.peers) < 2:
            self.peers.append(PeerSlot(addr=addr, last_seen=now))
            self.log.info(
                "event=peer_joined addr=%s slots_filled=%d",
                self._fmt_addr(addr),
                len(self.peers),
            )
            self.stats.pair_changes += 1
            if len(self.peers) == 2:
                self.log.info(
                    "event=peer_paired a=%s b=%s",
                    self._fmt_addr(self.peers[0].addr),
                    self._fmt_addr(self.peers[1].addr),
                )
            return len(self.peers) - 1
        # Both slots occupied. Replace the stalest one if it has been idle.
        oldest = 0 if self.peers[0].last_seen <= self.peers[1].last_seen else 1
        if (now - self.peers[oldest].last_seen) > IDLE_TIMEOUT_SECONDS:
            old_addr = self.peers[oldest].addr
            self.peers[oldest] = PeerSlot(addr=addr, last_seen=now)
            self.log.info(
                "event=peer_replaced old=%s new=%s",
                self._fmt_addr(old_addr),
                self._fmt_addr(addr),
            )
            self.stats.pair_changes += 1
            return oldest
        return -1  # Both slots active — packet from a third endpoint is ignored.

    def handle_packet(self, data: bytes, addr: tuple[str, int]) -> None:
        parsed = parse_header(data)
        if parsed is None:
            self.stats.rejected_bad_header += 1
            return
        # We do not log per-packet detail (would flood the log). Stats covers it.
        _version, _pkt_type, _stream_id, _sequence = parsed

        now = time.monotonic()
        idx = self.find_slot(addr)
        if idx is None:
            # Take the chance to age out idle slots first.
            self.expire_idle(now)
            idx = self.admit_or_replace(addr, now)
            if idx < 0:
                self.stats.dropped_unpaired += 1
                return

        peer = self.peers[idx]
        peer.last_seen = now
        peer.rx_packets += 1

        if len(self.peers) == 2:
            other = self.peers[1 - idx]
            try:
                self.sock.sendto(data, other.addr)
                other.tx_packets += 1
                self.stats.forwarded += 1
            except OSError as e:
                self.log.warning(
                    "event=send_failed to=%s err=%s",
                    self._fmt_addr(other.addr),
                    e,
                )
        else:
            self.stats.dropped_unpaired += 1

    def maybe_log_stats(self, now: float) -> None:
        if (now - self.last_stats_log) < STATS_INTERVAL_SECONDS:
            return
        self.last_stats_log = now
        s = self.stats
        peers_summary = ", ".join(
            f"{self._fmt_addr(p.addr)}(rx={p.rx_packets},tx={p.tx_packets})"
            for p in self.peers
        ) or "none"
        self.log.info(
            "event=stats forwarded=%d dropped_unpaired=%d rejected_bad_header=%d pair_changes=%d peers=[%s]",
            s.forwarded,
            s.dropped_unpaired,
            s.rejected_bad_header,
            s.pair_changes,
            peers_summary,
        )
        # Reset counters so the next stats line shows a per-minute rate.
        self.stats = RelayStats()
        for p in self.peers:
            p.rx_packets = 0
            p.tx_packets = 0


def main() -> int:
    parser = argparse.ArgumentParser(description="RemSound UDP relay")
    parser.add_argument(
        "--port",
        type=int,
        default=DEFAULT_PORT,
        help=f"UDP port to listen on (default {DEFAULT_PORT})",
    )
    parser.add_argument(
        "--host",
        default=LISTEN_HOST,
        help=f"Bind address (default {LISTEN_HOST})",
    )
    parser.add_argument(
        "--log-path",
        default=DEFAULT_LOG_PATH,
        help=f"Log file path (default {DEFAULT_LOG_PATH})",
    )
    args = parser.parse_args()

    log = setup_logger(args.log_path)
    log.info("event=startup version=1 listen=%s:%d", args.host, args.port)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        sock.bind((args.host, args.port))
    except OSError as e:
        log.error("event=bind_failed err=%s", e)
        return 1

    relay = Relay(sock, log)

    stop_flag = {"stop": False}

    def _stop_signal(_signum, _frame):
        stop_flag["stop"] = True

    signal.signal(signal.SIGTERM, _stop_signal)
    signal.signal(signal.SIGINT, _stop_signal)

    try:
        while not stop_flag["stop"]:
            try:
                ready, _, _ = select.select([sock], [], [], SOCKET_POLL_TIMEOUT_SECONDS)
            except InterruptedError:
                continue
            now = time.monotonic()
            if ready:
                try:
                    data, addr = sock.recvfrom(RECV_BUFFER_BYTES)
                except OSError as e:
                    log.warning("event=recv_failed err=%s", e)
                    continue
                relay.handle_packet(data, addr)
            relay.expire_idle(now)
            relay.maybe_log_stats(now)
    finally:
        log.info("event=shutdown")
        sock.close()

    return 0


if __name__ == "__main__":
    sys.exit(main())
