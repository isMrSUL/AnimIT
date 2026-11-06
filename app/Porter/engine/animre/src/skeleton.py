"""YOBJ (HCTP) and JBOY (SvR 2011) skeleton records and rest joints.

Offsets are relative to the complete, uncompressed model resource. Unknown
fields are exposed as raw bytes; parsing and serializing leaves them intact.
"""

from __future__ import annotations

import math
import struct
from dataclasses import dataclass


Vec3 = tuple[float, float, float]
Mat3 = tuple[Vec3, Vec3, Vec3]
IDENTITY: Mat3 = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))


def _matmul(a: Mat3, b: Mat3) -> Mat3:
    return tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3))
                       for j in range(3)) for i in range(3))  # type: ignore[return-value]


def _rotate(r: Mat3, v: Vec3) -> Vec3:
    return tuple(sum(r[i][k] * v[k] for k in range(3))
                 for i in range(3))  # type: ignore[return-value]


def euler_zyx(angles: Vec3) -> Mat3:
    """Rz(z) * Ry(y) * Rx(x), matching the existing SvR model viewer."""
    x, y, z = angles
    cx, sx = math.cos(x), math.sin(x)
    cy, sy = math.cos(y), math.sin(y)
    cz, sz = math.cos(z), math.sin(z)
    return ((cz * cy, cz * sy * sx - sz * cx, cz * sy * cx + sz * sx),
            (sz * cy, sz * sy * sx + cz * cx, sz * sy * cx - cz * sx),
            (-sy, cy * sx, cy * cx))


@dataclass(frozen=True)
class Bone:
    index: int
    name: str
    parent: int
    translation: tuple[float, float, float, float]
    euler: tuple[float, float, float, float]
    extra_34: bytes
    extra_40: tuple[float, float, float, float]
    offset: int


@dataclass(frozen=True)
class Joint:
    position: Vec3
    rotation: Mat3


@dataclass
class Skeleton:
    kind: str
    bones: list[Bone]
    raw: bytes
    table_offset: int

    def to_bytes(self) -> bytes:
        """No-edit serialization preserves the complete model byte for byte."""
        return bytes(self.raw)

    def joints(self) -> list[Joint]:
        """Compose local rest transforms through parents, in model coordinates."""
        out: list[Joint | None] = [None] * len(self.bones)
        visiting: set[int] = set()

        def resolve(index: int) -> Joint:
            if out[index] is not None:
                return out[index]
            if index in visiting:
                raise ValueError(f"cyclic bone hierarchy at {index}")
            visiting.add(index)
            bone = self.bones[index]
            local_t = bone.translation[:3]
            local_r = euler_zyx(bone.euler[:3])
            if bone.parent >= 0:
                parent = resolve(bone.parent)
                step = _rotate(parent.rotation, local_t)
                position = tuple(parent.position[i] + step[i] for i in range(3))
                rotation = _matmul(parent.rotation, local_r)
            else:
                position, rotation = local_t, local_r
            out[index] = Joint(position, rotation)
            visiting.remove(index)
            return out[index]

        return [resolve(i) for i in range(len(self.bones))]

    def name_index(self) -> dict[str, int]:
        """Map unique bone names to indices; reject ambiguous model tables."""
        result: dict[str, int] = {}
        for bone in self.bones:
            if bone.name in result:
                raise ValueError(f"duplicate bone name {bone.name!r}")
            result[bone.name] = bone.index
        return result


def exact_name_map(source: Skeleton, target: Skeleton) -> dict[int, int]:
    """Conservative cross-game map; unmatched names need manual evaluation."""
    lookup = target.name_index()
    return {bone.index: lookup[bone.name] for bone in source.bones
            if bone.name in lookup}


def parse(data: bytes) -> Skeleton:
    """Read the bone table from one decompressed YOBJ or JBOY resource."""
    if len(data) < 0x40:
        raise ValueError("model header is truncated")
    if data[:4] == b"YOBJ":
        kind, endian, count_at, table_at = "YOBJ", "<", 0x1c, 0x28
    elif data[:4] == b"JBOY":
        kind, endian, count_at, table_at = "JBOY", ">", 0x20, 0x28
    else:
        raise ValueError(f"not a YOBJ/JBOY resource: {data[:4]!r}")
    count = struct.unpack_from(endian + "I", data, count_at)[0]
    table = struct.unpack_from(endian + "I", data, table_at)[0] + 8
    if count > 4096 or table < 8 or table + count * 0x50 > len(data):
        raise ValueError(f"bad {kind} bone table: {count=} {table=:#x}")
    bones: list[Bone] = []
    for index in range(count):
        at = table + index * 0x50
        name = data[at:at + 0x10].split(b"\0", 1)[0].decode("latin1")
        t = struct.unpack_from(endian + "4f", data, at + 0x10)
        e = struct.unpack_from(endian + "4f", data, at + 0x20)
        parent = struct.unpack_from(endian + "i", data, at + 0x30)[0]
        if parent == 0xffff:
            parent = -1
        if parent >= count or parent < -1 or parent == index:
            raise ValueError(f"bad {kind} parent {parent} for bone {index} {name!r}")
        extra_34 = data[at + 0x34:at + 0x40]
        extra_40 = struct.unpack_from(endian + "4f", data, at + 0x40)
        bones.append(Bone(index, name, parent, t, e, extra_34, extra_40, at))
    return Skeleton(kind, bones, bytes(data), table)
