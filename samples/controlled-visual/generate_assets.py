"""Deterministic 32x32 BMP fixtures; no captured images or runtime screenshots."""
import struct
from pathlib import Path

root = Path(__file__).parent / 'plugin'
for kind, name in enumerate(('button', 'complete', 'blocked')):
    rows = bytearray()
    for y in range(31, -1, -1):
        for x in range(32):
            tile = ((x // 4) * 3 + (y // 4) * 5 + kind * 7) % 11
            r = (tile * 41 + kind * 67 + 23) % 256
            g = (tile * 71 + kind * 43 + 59) % 256
            b = (tile * 97 + kind * 109 + 11) % 256
            if x in (0, 31) or y in (0, 31):
                r, g, b = 245, 245, 245
            rows.extend((b, g, r))
    header = b'BM' + struct.pack('<IHHI', 54 + len(rows), 0, 0, 54)
    header += struct.pack('<IiiHHIIiiII', 40, 32, 32, 1, 24, 0, len(rows), 2835, 2835, 0, 0)
    (root / (name + '.bmp')).write_bytes(header + rows)
