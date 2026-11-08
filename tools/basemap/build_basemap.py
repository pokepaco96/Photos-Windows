#!/usr/bin/env python3
"""Builds the embedded world basemap (world.pwmap) from Natural Earth public-domain vectors.

Reproducible: inputs are pinned by URL + SHA-256, only the Python standard library is used, and the
output is byte-for-byte deterministic. Usage:

    python3 tools/basemap/build_basemap.py --cache <dir> [--download] --out src/PhotosWindows.Imaging/Maps/world.pwmap

Without --download the four ZIPs must already be in <dir> (e.g. downloaded by hand from naturalearthdata.com).

Output format (little-endian):
    "PWMAP\\x01\\x00\\x00"  8-byte magic (version 1)
    raw DEFLATE stream of:
        uint8 layerCount
        per layer: uint8 kind (0 = filled polygon rings, 1 = lines), uint8 nameLen, name (ASCII),
                   varuint ringCount,
                   per ring: varuint pointCount, then pointCount x (zigzag varint dLon, zigzag varint dLat)
                   in units of 1e-4 degree, delta-coded from the previous point of the ring (first from 0,0).
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import struct
import sys
import urllib.request
import zipfile
import zlib
from pathlib import Path

SOURCES = [
    # (layer name, kind, file stem, url, sha256, Natural Earth version)
    ("land110", 0, "ne_110m_land", "https://naciscdn.org/naturalearth/110m/physical/ne_110m_land.zip",
     "1926c621afd6ac67c3f36639bb1236134a48d82226dc675d3e3df53d02d2a3de", "4.1.0"),
    ("land50", 0, "ne_50m_land", "https://naciscdn.org/naturalearth/50m/physical/ne_50m_land.zip",
     "0b8e670cf80dce9cbebe2a193bc44ba5602758c22e1fa603980553646d7ff162", "4.1.0"),
    ("lakes50", 0, "ne_50m_lakes", "https://naciscdn.org/naturalearth/50m/physical/ne_50m_lakes.zip",
     "f28d42c286d96b57a17aac2cbeb432f8c65532c20063495711fbc64e24666df3", "5.0.0"),
    ("borders50", 1, "ne_50m_admin_0_boundary_lines_land",
     "https://naciscdn.org/naturalearth/50m/cultural/ne_50m_admin_0_boundary_lines_land.zip",
     "02f369ae10578c4ce33fc4a08f8cdd69ea11eb39b44d7923e88eb7199aaff8ea", "5.1.0"),
]
SCALE = 10_000  # 1e-4 degree ≈ 11 m at the equator; far finer than the 1:50m source.
MAGIC = b"PWMAP\x01\x00\x00"


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_shp_parts(shp: bytes) -> list[list[tuple[float, float]]]:
    """Minimal ESRI shapefile reader for Polygon (5) and PolyLine (3) records."""
    file_len = struct.unpack(">i", shp[24:28])[0] * 2
    pos = 100
    parts_out: list[list[tuple[float, float]]] = []
    while pos < file_len:
        _, content_words = struct.unpack(">ii", shp[pos:pos + 8])
        rec = shp[pos + 8: pos + 8 + content_words * 2]
        pos += 8 + content_words * 2
        shape_type = struct.unpack("<i", rec[0:4])[0]
        if shape_type == 0:
            continue
        if shape_type not in (3, 5):
            raise ValueError(f"unsupported shape type {shape_type}")
        num_parts, num_points = struct.unpack("<ii", rec[36:44])
        parts = list(struct.unpack(f"<{num_parts}i", rec[44:44 + 4 * num_parts])) + [num_points]
        pts_off = 44 + 4 * num_parts
        coords = struct.unpack(f"<{2 * num_points}d", rec[pts_off:pts_off + 16 * num_points])
        for i in range(num_parts):
            ring = [(coords[2 * k], coords[2 * k + 1]) for k in range(parts[i], parts[i + 1])]
            if len(ring) >= 2:
                parts_out.append(ring)
    return parts_out


def varuint(v: int, out: bytearray) -> None:
    while True:
        b = v & 0x7F
        v >>= 7
        if v:
            out.append(b | 0x80)
        else:
            out.append(b)
            return


def zigzag(v: int) -> int:
    return (v << 1) ^ (v >> 63)


def encode_layer(name: str, kind: int, rings: list[list[tuple[float, float]]], out: bytearray) -> int:
    out.append(kind)
    out.append(len(name))
    out.extend(name.encode("ascii"))
    encoded = []
    for ring in rings:
        q = []
        for lon, lat in ring:
            p = (round(lon * SCALE), round(lat * SCALE))
            if not q or q[-1] != p:  # quantisation can collapse neighbouring vertices
                q.append(p)
        if len(q) >= 2:
            encoded.append(q)
    varuint(len(encoded), out)
    points = 0
    for q in encoded:
        varuint(len(q), out)
        px = py = 0
        for x, y in q:
            varuint(zigzag(x - px), out)
            varuint(zigzag(y - py), out)
            px, py = x, y
        points += len(q)
    return points


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", required=True, type=Path)
    ap.add_argument("--out", required=True, type=Path)
    ap.add_argument("--download", action="store_true", help="download missing ZIPs from naciscdn.org")
    args = ap.parse_args()
    args.cache.mkdir(parents=True, exist_ok=True)

    payload = bytearray([len(SOURCES)])
    manifest = {"format": "pwmap/1", "scale": SCALE, "license": "Public domain (Natural Earth)", "layers": []}
    for name, kind, stem, url, expected, version in SOURCES:
        zpath = args.cache / f"{stem}.zip"
        if not zpath.exists():
            if not args.download:
                print(f"missing {zpath}; pass --download or fetch {url}", file=sys.stderr)
                return 2
            with urllib.request.urlopen(url, timeout=60) as r:
                zpath.write_bytes(r.read())
        data = zpath.read_bytes()
        actual = sha256(data)
        if actual != expected:
            print(f"SHA-256 mismatch for {zpath.name}: {actual} != {expected}", file=sys.stderr)
            return 3
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            rings = read_shp_parts(z.read(f"{stem}.shp"))
        points = encode_layer(name, kind, rings, payload)
        manifest["layers"].append({"name": name, "kind": "fill" if kind == 0 else "line", "source": url,
                                   "sha256": expected, "naturalEarthVersion": version,
                                   "rings": len(rings), "points": points})

    comp = zlib.compressobj(9, zlib.DEFLATED, -15, 9)
    blob = MAGIC + comp.compress(bytes(payload)) + comp.flush()
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_bytes(blob)
    manifest["bytes"] = len(blob)
    manifest["uncompressedPayloadBytes"] = len(payload)
    manifest["sha256"] = sha256(blob)
    args.out.with_suffix(".manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
