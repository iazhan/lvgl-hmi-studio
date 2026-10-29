#!/usr/bin/env python3
"""png2bin.py — PNG 转组态图片 .bin（37 §6.15）

格式：32 字节头 + 像素数据（小端）
  0  magic "IMG1" | 4 header_size=32 | 6 version=1 | 8 width | 10 height
  12 format(0=RGB565, 1=RGB565A8) | 13 flags=0 | 14 reserved
  16 stride（RGB565 平面每行字节数 = width*2） | 20 data_size
  24 crc32（对像素数据） | 28 reserved

用法：
  png2bin.py <输入.png> <输出.bin> [--alpha]
  不带 --alpha 时忽略透明通道（透明像素按黑底混合）；带 --alpha 时输出 RGB565A8。

依赖 Pillow（仅宿主侧工具，不进固件）。
"""
import struct
import sys
import zlib

from PIL import Image

HEADER_SIZE = 32
MAX_SIDE = 2048
MAX_BYTES = 2 * 1024 * 1024


def build(im, force_alpha):
    im = im.convert("RGBA")
    w, h = im.size
    if not (1 <= w <= MAX_SIDE and 1 <= h <= MAX_SIDE):
        raise SystemExit("尺寸 %dx%d 超出 1..%d" % (w, h, MAX_SIDE))

    px = im.load()
    has_alpha = force_alpha or any(px[x, y][3] < 255 for y in range(h) for x in range(w))

    rgb = bytearray()
    a8 = bytearray()
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if not has_alpha:
                # 透明按黑底混合：否则 RGB565 里会留下原色的边
                r = r * a // 255
                g = g * a // 255
                b = b * a // 255
            rgb += struct.pack("<H", ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3))
            if has_alpha:
                a8.append(a)

    data = bytes(rgb) + bytes(a8)
    if len(data) > MAX_BYTES:
        raise SystemExit("像素数据 %.1f MB 超过 2MB 上限" % (len(data) / 1048576.0))

    header = struct.pack("<4sHHHHBBHIIII",
                         b"IMG1", HEADER_SIZE, 1, w, h,
                         1 if has_alpha else 0, 0, 0,
                         w * 2, len(data), zlib.crc32(data) & 0xFFFFFFFF, 0)
    return header + data, has_alpha


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    src, dst = sys.argv[1], sys.argv[2]
    force_alpha = "--alpha" in sys.argv[3:]

    im = Image.open(src)
    blob, has_alpha = build(im, force_alpha)
    with open(dst, "wb") as f:
        f.write(blob)
    print("%s → %s：%dx%d，%s，%d 字节%s"
          % (src, dst, im.size[0], im.size[1],
             "RGB565A8" if has_alpha else "RGB565", len(blob),
             "（强制带 alpha）" if force_alpha and not any(
                 p[3] < 255 for p in im.convert("RGBA").getdata()) else ""))


if __name__ == "__main__":
    main()
