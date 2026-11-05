"""Generates src/AiPhoto.App/Assets/AppIcon.ico (multi-size, PNG-compressed entries) without third-party libs.

Design: rounded square with a blue→violet gradient, white capture-corner brackets, two "text lines"
and a small four-point sparkle (AI) in the top-right corner.
"""
import math
import struct
import sys
import zlib

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 4  # supersampling factor


def lerp(a, b, t):
    return a + (b - a) * t


def rounded_rect_sdf(x, y, cx, cy, hw, hh, r):
    qx = abs(x - cx) - (hw - r)
    qy = abs(y - cy) - (hh - r)
    ox, oy = max(qx, 0.0), max(qy, 0.0)
    return math.hypot(ox, oy) + min(max(qx, qy), 0.0) - r


def box_sdf(x, y, x0, y0, x1, y1):
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    return rounded_rect_sdf(x, y, cx, cy, (x1 - x0) / 2, (y1 - y0) / 2, min(x1 - x0, y1 - y0) / 2 * 0.999)


def sparkle_sdf(x, y, cx, cy, r):
    # 4-point star: union of two thin diamonds
    dx, dy = abs(x - cx), abs(y - cy)
    d1 = (dx / (r * 0.28) + dy / r) - 1.0
    d2 = (dx / r + dy / (r * 0.28)) - 1.0
    return min(d1, d2) * r * 0.3


def sample(u, v):
    """u, v in [0, 1]. Returns premultiplied RGBA floats."""
    # Background
    bg = rounded_rect_sdf(u, v, 0.5, 0.5, 0.47, 0.47, 0.2)
    if bg > 0:
        return (0.0, 0.0, 0.0, 0.0)
    t = (u + v) / 2
    r, g, b = lerp(0.16, 0.49, t), lerp(0.47, 0.30, t), lerp(0.98, 0.93, t)
    color = [r, g, b]

    white = 0.0
    # Corner brackets
    m0, m1, L, w = 0.2, 0.8, 0.17, 0.065
    for (cx, cy, sx, sy) in [(m0, m0, 1, 1), (m1, m0, -1, 1), (m0, m1, 1, -1), (m1, m1, -1, -1)]:
        hx0, hx1 = sorted([cx, cx + sx * L])
        hy0, hy1 = sorted([cy, cy + sy * w])
        vx0, vx1 = sorted([cx, cx + sx * w])
        vy0, vy1 = sorted([cy, cy + sy * L])
        if box_sdf(u, v, hx0, hy0, hx1, hy1) <= 0 or box_sdf(u, v, vx0, vy0, vx1, vy1) <= 0:
            white = 1.0
    # Text lines
    if box_sdf(u, v, 0.34, 0.50, 0.66, 0.57) <= 0 or box_sdf(u, v, 0.34, 0.62, 0.58, 0.69) <= 0:
        white = 1.0
    # Sparkle
    if sparkle_sdf(u, v, 0.50, 0.335, 0.115) <= 0:
        white = 1.0

    if white:
        color = [1.0, 1.0, 1.0]
    return (color[0], color[1], color[2], 1.0)


def render(size):
    pixels = bytearray()
    n = SS * SS
    for py in range(size):
        pixels.append(0)  # PNG filter type None
        for px in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(SS):
                for sx in range(SS):
                    u = (px + (sx + 0.5) / SS) / size
                    v = (py + (sy + 0.5) / SS) / size
                    r, g, b, a = sample(u, v)
                    acc[0] += r * a
                    acc[1] += g * a
                    acc[2] += b * a
                    acc[3] += a
            a = acc[3] / n
            if a > 0:
                rgb = [min(255, int(round(c / acc[3] * 255))) for c in acc[:3]]
            else:
                rgb = [0, 0, 0]
            pixels.extend(bytes(rgb + [int(round(a * 255))]))
    return png(size, size, bytes(pixels))


def png(w, h, raw):
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main(out_path, png_path=None):
    images = [(s, render(s)) for s in SIZES]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, data = b"", b""
    for size, blob in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset + len(data))
        data += blob
    with open(out_path, "wb") as f:
        f.write(header + entries + data)
    if png_path:
        with open(png_path, "wb") as f:
            f.write(images[-1][1])


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
