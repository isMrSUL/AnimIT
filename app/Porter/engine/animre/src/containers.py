"""Read and write the archive containers used by HCTP and SvR 2011.

The original layout, including padding and unused bytes, is retained on a
same-size replacement. A size-changing replacement repacks the archive with
the alignment used by its format. BPE retains its original compressed stream
until its decoded payload is replaced.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from functools import lru_cache


def _u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def _u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def _align(value: int, alignment: int) -> int:
    return (value + alignment - 1) // alignment * alignment


@dataclass
class Entry:
    name: str
    offset: int
    size: int
    data: bytes
    size_field: int | None = None
    offset_field: int | None = None
    numbered: bool = False
    group: int = 0


class Archive:
    """Parsed archive with indexed entries and replaceable stored payloads."""

    def __init__(self, kind: str, raw: bytes, entries: list[Entry],
                 groups: list[tuple[int, list[int]]] | None = None):
        self.kind = kind
        self.raw = raw
        self.entries = entries
        self.groups = groups or []
        self.replacements: dict[int, bytes] = {}

    def replace(self, index: int, payload: bytes) -> None:
        if not 0 <= index < len(self.entries):
            raise IndexError(index)
        self.replacements[index] = bytes(payload)

    def to_bytes(self) -> bytes:
        if self.kind == "BPE":
            if not self.replacements:
                return bytes(self.raw)
            if set(self.replacements) != {0}:
                raise ValueError("BPE has one decoded payload")
            return bpe_encode(self.replacements[0])
        if not self.replacements or all(
            len(payload) == self.entries[i].size for i, payload in self.replacements.items()
        ):
            out = bytearray(self.raw)
            for i, payload in self.replacements.items():
                entry = self.entries[i]
                out[entry.offset:entry.offset + entry.size] = payload
            return bytes(out)
        if self.kind in ("PAC", "PACH"):
            return self._repack_linear()
        if self.kind in ("DPAC", "EPAC", "EPK8"):
            return self._repack_sector()
        raise ValueError(f"cannot repack {self.kind}")

    def _repack_linear(self) -> bytes:
        table_end = 8 + len(self.entries) * (8 if self.kind == "PAC" else 12)
        out = bytearray(self.raw[:table_end])
        for i, entry in enumerate(self.entries):
            payload = self.replacements.get(i, entry.data)
            if self.kind == "PACH":
                out += b"\0" * (-len(out) % 4)
                struct.pack_into("<II", out, 8 + 12 * i + 4,
                                 len(out) - table_end, len(payload))
            else:
                rel = len(out) - table_end
                if rel >= 1 << 24 or len(payload) >= 1 << 24:
                    raise ValueError("PAC 24-bit offset/size overflow")
                at = 8 + 8 * i + 2
                out[at:at + 3] = rel.to_bytes(3, "little")
                out[at + 3:at + 6] = len(payload).to_bytes(3, "little")
            out += payload
        return bytes(out)

    def _repack_sector(self) -> bytes:
        out = bytearray(self.raw[:0x4000])
        if len(out) != 0x4000:
            raise ValueError("sector archive header is truncated")
        if self.kind == "DPAC":
            for group_field, indices in self.groups:
                if indices and self.entries[indices[0]].numbered:
                    out += b"\0" * (-len(out) % 0x800)
                    struct.pack_into("<H", out, group_field,
                                     (len(out) - 0x4000) // 0x800)
                for i in indices:
                    entry = self.entries[i]
                    payload = self.replacements.get(i, entry.data)
                    out += b"\0" * (-len(out) % 0x800)
                    sector = (len(out) - 0x4000) // 0x800
                    units = _align(len(payload), 0x100) // 0x100
                    if sector > 0xffff or units > 0xffff:
                        raise ValueError("DPAC sector/size overflow")
                    if not entry.numbered:
                        struct.pack_into("<H", out, entry.offset_field, sector)
                    struct.pack_into("<H", out, entry.size_field, units)
                    out += payload + bytes(_align(len(payload), 0x100) - len(payload))
            out += b"\0" * (-len(out) % 0x800)
            return bytes(out)
        for i, entry in enumerate(self.entries):
            payload = self.replacements.get(i, entry.data)
            out += b"\0" * (-len(out) % 0x800)
            sector = (len(out) - 0x4000) // 0x800
            units = _align(len(payload), 0x100) // 0x100
            struct.pack_into("<II", out, entry.offset_field, sector, units)
            out += payload + bytes(_align(len(payload), 0x100) - len(payload))
        out += b"\0" * (-len(out) % 0x800)
        if self.kind == "EPAC":
            struct.pack_into("<I", out, 8, len(out) - 0x4000)
            # EPAC's optional packer footer follows the data region.
            old_data_end = 0x4000 + _u32(self.raw, 8)
            out += self.raw[old_data_end:]
        else:
            out += self.raw[-0x800:]
            struct.pack_into("<I", out, 8, len(out) - 0x4800)
        return bytes(out)


def _checked_slice(raw: bytes, offset: int, size: int) -> bytes:
    if offset < 0 or size < 0 or offset + size > len(raw):
        raise ValueError(f"entry range {offset:#x}+{size:#x} outside {len(raw):#x}")
    return raw[offset:offset + size]


def parse(raw: bytes) -> Archive:
    if len(raw) < 8:
        raise ValueError("archive is too short")
    magic = raw[:4]
    if magic == b"PAC ":
        return parse_pac(raw)
    if magic == b"DPAC":
        return parse_dpac(raw)
    if magic == b"PACH":
        return parse_pach(raw)
    if magic == b"EPAC":
        return parse_epac(raw)
    if magic == b"EPK8":
        return parse_epk8(raw)
    if magic == b"BPE ":
        decoded = bpe_decode(raw)
        return Archive("BPE", raw, [Entry("decoded", 0, len(decoded), decoded)])
    raise ValueError(f"unknown archive magic {magic!r}")


def parse_pac(raw: bytes) -> Archive:
    count = _u32(raw, 4)
    base = 8 + 8 * count
    if count > 200000 or base > len(raw):
        raise ValueError("bad PAC directory")
    entries = []
    for i in range(count):
        p = 8 + 8 * i
        name = f"{_u16(raw, p):04x}"
        off = int.from_bytes(raw[p + 2:p + 5], "little")
        size = int.from_bytes(raw[p + 5:p + 8], "little")
        start = base + off
        entries.append(Entry(name, start, size, _checked_slice(raw, start, size),
                             size_field=p + 5, offset_field=p + 2))
    return Archive("PAC", raw, entries)


def parse_pach(raw: bytes) -> Archive:
    count = _u32(raw, 4)
    base = 8 + 12 * count
    if count > 200000 or base > len(raw):
        raise ValueError("bad PACH directory")
    entries = []
    for i in range(count):
        p = 8 + 12 * i
        ident, off, size = struct.unpack_from("<III", raw, p)
        start = base + off
        entries.append(Entry(f"{ident:x}", start, size,
                             _checked_slice(raw, start, size),
                             size_field=p + 8, offset_field=p + 4))
    return Archive("PACH", raw, entries)


def parse_dpac(raw: bytes) -> Archive:
    if len(raw) < 0x4000:
        raise ValueError("DPAC header is truncated")
    p, end = 0x800, 0x800 + _u32(raw, 4)
    if end > 0x4000:
        raise ValueError("DPAC directory exceeds header")
    entries: list[Entry] = []
    groups: list[tuple[int, list[int]]] = []
    while p + 8 <= end:
        typ = raw[p:p + 4].decode("latin1").strip(" \0")
        count, sector = struct.unpack_from("<HH", raw, p + 4)
        group_field = p + 6
        p += 8
        indices = []
        if count & 0x8000:
            start = 0x4000 + sector * 0x800
            for _ in range(count & 0x7fff):
                if p + 4 > end:
                    raise ValueError("DPAC numbered directory overrun")
                key, units = struct.unpack_from("<HH", raw, p)
                size = units * 0x100
                indices.append(len(entries))
                entries.append(Entry(f"{typ}/{key:04x}", start, size,
                                     _checked_slice(raw, start, size),
                                     size_field=p + 2, numbered=True,
                                     group=len(groups)))
                start = _align(start + size, 0x800)
                p += 4
        else:
            for _ in range(count // 2):
                if p + 8 > end:
                    raise ValueError("DPAC named directory overrun")
                name = raw[p:p + 4].decode("latin1").strip(" \0")
                sec, units = struct.unpack_from("<HH", raw, p + 4)
                start, size = 0x4000 + sec * 0x800, units * 0x100
                indices.append(len(entries))
                entries.append(Entry(f"{typ}/{name}", start, size,
                                     _checked_slice(raw, start, size),
                                     size_field=p + 6, offset_field=p + 4,
                                     group=len(groups)))
                p += 8
        groups.append((group_field, indices))
    return Archive("DPAC", raw, entries, groups)


def parse_epac(raw: bytes) -> Archive:
    if len(raw) < 0x4000:
        raise ValueError("EPAC header is truncated")
    p = 0x800
    entries = []
    while p + 12 <= 0x4000 and raw[p:p + 4] != b"\0" * 4:
        typ = raw[p:p + 4].decode("latin1").strip(" \0")
        count = _u32(raw, p + 4) // 3
        p += 12
        if count > 200000 or p + count * 12 > 0x4000:
            raise ValueError("bad EPAC group")
        for _ in range(count):
            name = raw[p:p + 4].decode("latin1").strip(" \0")
            sector, units = struct.unpack_from("<II", raw, p + 4)
            start, size = 0x4000 + sector * 0x800, units * 0x100
            entries.append(Entry(f"{typ}/{name}", start, size,
                                 _checked_slice(raw, start, size),
                                 size_field=p + 8, offset_field=p + 4))
            p += 12
    return Archive("EPAC", raw, entries)


def parse_epk8(raw: bytes) -> Archive:
    if len(raw) < 0x4800:
        raise ValueError("EPK8 header/footer is truncated")
    marker = raw[0x800:0x804]
    if marker != b"EMD " and not marker.startswith(b"EVP"):
        raise ValueError(f"unsupported EPK8 table {marker!r}")
    p = 0x80c
    entries = []
    while p + 16 <= 0x4000:
        label = raw[p:p + 8]
        if not label.strip(b" \0"):
            break
        if marker == b"EMD " and not label.isdigit():
            break
        if marker.startswith(b"EVP") and not all(32 <= b <= 126 for b in label):
            break
        name = label.decode("ascii").strip()
        sector, units = struct.unpack_from("<II", raw, p + 8)
        start, size = 0x4000 + sector * 0x800, units * 0x100
        entries.append(Entry(name, start, size,
                             _checked_slice(raw, start, size),
                             size_field=p + 12, offset_field=p + 8))
        p += 16
    if not entries:
        raise ValueError("EPK8 has no EMD sections")
    return Archive("EPK8", raw, entries)


def bpe_decode(raw: bytes) -> bytes:
    if raw[:4] != b"BPE ":
        raise ValueError("not BPE")
    # The reference implementation is used here until the BPE codec is audited
    # against every game block. It is pure Python and reads no external files.
    return _svrfmt().bpe_decode(raw)


def bpe_encode(decoded: bytes) -> bytes:
    return _svrfmt().bpe_encode(decoded)


@lru_cache(maxsize=1)
def _svrfmt():
    import svrfmt as module
    return module
