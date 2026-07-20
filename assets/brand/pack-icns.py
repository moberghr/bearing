#!/usr/bin/env python3
"""Pack a set of PNGs into a macOS .icns file.

Modern .icns entries carry PNG data verbatim under a 4-char OSType. We emit the
common ramp so Finder/Dock pick the right size on both standard and Retina.

Usage: pack-icns.py <out.icns> <size>:<png> [<size>:<png> ...]
"""
import struct
import sys

# OSType for a PNG payload at each pixel size. Where a size has both a
# non-retina and a retina slot we prefer the plain one; adding the "@2x"
# variants (ic11/ic12/ic13/ic14) lets Retina displays pick a denser source.
TYPE_FOR_SIZE = {
    16: b"icp4",
    32: b"icp5",
    64: b"icp6",
    128: b"ic07",
    256: b"ic08",
    512: b"ic09",
    1024: b"ic10",
}
# Retina slots (physical pixels -> logical @2x OSType).
RETINA_FOR_SIZE = {
    32: b"ic11",   # 16pt @2x
    64: b"ic12",   # 32pt @2x
    256: b"ic13",  # 128pt @2x
    512: b"ic14",  # 256pt @2x
}


def main():
    out = sys.argv[1]
    entries = []  # (ostype, data)
    seen_types = set()
    for arg in sys.argv[2:]:
        size_s, path = arg.split(":", 1)
        size = int(size_s)
        with open(path, "rb") as f:
            data = f.read()
        for table in (TYPE_FOR_SIZE, RETINA_FOR_SIZE):
            ostype = table.get(size)
            if ostype and ostype not in seen_types:
                entries.append((ostype, data))
                seen_types.add(ostype)

    body = b"".join(
        ostype + struct.pack(">I", len(data) + 8) + data for ostype, data in entries
    )
    total = 8 + len(body)
    with open(out, "wb") as f:
        f.write(b"icns" + struct.pack(">I", total) + body)
    print(f"wrote {out}: {len(entries)} entries, {total} bytes")


if __name__ == "__main__":
    main()
