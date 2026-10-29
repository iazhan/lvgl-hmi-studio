#!/usr/bin/env python3
"""设计画布取图：把宿主渲染出的那一帧存成 PNG，供人工对比。

产物（默认落在 logs/scratch/design-shot/frame.png）就是**设计画布上屏用的那份
像素**：设计画布在真机渲染模式下直接把这张位图画到画布上，缩放 1:1 时两者应当
完全一致；不一致就在"共享内存 → 客户端位图 → 画布"这一段（通道序、stride、
header_bytes 偏移、缩放取整）。

只出 PNG，不出宿主自截的 BMP：预览宿主是 headless，chenge_embed_save_bmp 写的是
SDL 窗口缓冲（s_buf），无窗口时该缓冲为 NULL，所以 --shot 与 S 命令在这个宿主里
本来就不可用。

设计模式刻意不推进时间（--design），同一份工程每次跑出来的像素都应当一样 ——
这正是能拿两张图直接比的前提。

用法：
    python3 design_shot.py --project <工程目录> [--out <输出目录>] [--show]

    --project  导出后的 chenge 工程目录（含 manifest.json 与 pages/）
    --out      输出目录，默认 logs/scratch/design-shot
    --show     跑完打印每个文件的尺寸（不做像素比较）
"""

import argparse
import mmap
import os
import select
import struct
import subprocess
import sys
import time
import zlib

MAGIC = 0x46474843  # 'CHGF'
FRAME_VERSION = 2


def fail(msg):
    print("FAIL:", msg)
    sys.exit(1)


def locate_host():
    """优先 CHENGE_HOST，其次仓库里的默认构建产物。"""
    env = os.environ.get("CHENGE_HOST")
    if env and os.path.exists(env):
        return env
    here = os.path.dirname(os.path.abspath(__file__))
    guess = os.path.join(os.path.dirname(here), "chenge-preview-host")
    if os.path.exists(guess):
        return guess
    fail("找不到 chenge-preview-host：设 CHENGE_HOST，或先在 chenge-runtime 里 make PREVIEW=1")


def read_header(mm):
    """(magic, version, w, h, stride, header_bytes, seq, applied_epoch, applied_seq)。"""
    return struct.unpack_from("<IIIIIIQII", mm, 0), 36


def wait_frame(mm, timeout):
    """等首帧（seq >= 1），返回 header 元组。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        hdr, _ = read_header(mm)
        if hdr[0] == MAGIC and hdr[5] > 0 and hdr[6] >= 1 and hdr[2] > 0 and hdr[3] > 0:
            return hdr
        time.sleep(0.01)
    return None


def write_png(path, width, height, stride, frame_bytes, pixel_offset):
    """BGRA（LVGL 32bpp 的字节序）→ RGBA PNG。"""
    rows = []
    for y in range(height):
        start = pixel_offset + y * stride
        line = bytearray(frame_bytes[start:start + width * 4])
        # BGRA -> RGBA：交换 R 与 B
        line[0::4], line[2::4] = line[2::4], line[0::4]
        rows.append(bytes(line))

    raw = b"".join(b"\x00" + r for r in rows)   # 每行前面加 filter byte 0
    comp = zlib.compress(raw, 6)

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", ihdr))
        f.write(chunk(b"IDAT", comp))
        f.write(chunk(b"IEND", b""))


def main():
    ap = argparse.ArgumentParser(description="设计画布取图（宿主自截 + 共享内存帧）")
    ap.add_argument("--project", required=True, help="chenge 工程目录")
    ap.add_argument("--out", default="logs/scratch/design-shot", help="输出目录")
    ap.add_argument("--timeout", type=float, default=20.0, help="等首帧的超时秒数")
    ap.add_argument("--show", action="store_true", help="跑完打印文件大小（不做像素比较）")
    args = ap.parse_args()

    if not os.path.isfile(os.path.join(args.project, "manifest.json")):
        fail(f"{args.project} 不像 chenge 工程（没有 manifest.json）")

    host = locate_host()
    out_dir = os.path.abspath(args.out)
    os.makedirs(out_dir, exist_ok=True)

    # 宿主需要一个可写数据目录（字体缓存、截图等）
    data_dir = os.path.join(out_dir, "data")
    os.makedirs(data_dir, exist_ok=True)

    name = f"/chenge-shot-{os.getpid()}-{int(time.time()) % 10000}"
    shm_path = os.path.join("/dev/shm", name.lstrip("/"))
    frame_png = os.path.join(out_dir, "frame.png")

    for stale in (shm_path, frame_png):
        if os.path.exists(stale):
            os.remove(stale)

    # 不用 --shot：那条走的是"帧计数到 20 再存"，设计模式没有渲染节拍，
    # 与其赌计数，不如用命令通道让宿主立刻存一次（S 与 R/L 同一条路，失败也能拿到原因）。
    proc = subprocess.Popen(
        [host, "--project", args.project, "--data", data_dir, "--shm", name, "--design"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    try:
        # READY 在首帧之后才打印（宿主侧保证），收到就可以读共享内存
        ready = None
        deadline = time.time() + args.timeout
        while time.time() < deadline:
            r, _, _ = select.select([proc.stdout], [], [], 0.2)
            if not r:
                if proc.poll() is not None:
                    break
                continue
            line = proc.stdout.readline()
            if line.startswith("READY"):
                ready = line.strip()
                break
        if ready is None:
            err = proc.stderr.read() if proc.poll() is not None else ""
            fail(f"宿主没打 READY（exit={proc.poll()}）：{err[-600:]}")

        # 等通道文件出现（宿主建完 shm 才会 READY，但文件落盘有极短延迟）
        for _ in range(200):
            if os.path.exists(shm_path):
                break
            time.sleep(0.01)
        else:
            fail(f"共享内存 {shm_path} 没出现")

        with open(shm_path, "rb") as f:
            with mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) as mm:
                hdr = wait_frame(mm, args.timeout)
                if hdr is None:
                    fail("等了很久没有首帧（seq 一直为 0）")
                magic, version, w, h, stride, pixel_off, seq, ep, sq = hdr
                if magic != MAGIC:
                    fail(f"帧头魔数不对：0x{magic:08x}")
                if version != FRAME_VERSION:
                    fail(f"帧头版本 {version}，期望 {FRAME_VERSION}（宿主与脚本不匹配）")
                if pixel_off <= 0 or pixel_off >= 4096:
                    fail(f"header_bytes={pixel_off} 不合理")

                frame = mm[pixel_off:pixel_off + stride * h]
                if len(frame) != stride * h:
                    fail(f"帧数据不完整：{len(frame)} / {stride * h}")

        write_png(frame_png, w, h, stride, frame, 0)

        if proc.stdin:
            proc.stdin.write("Q\n")
            proc.stdin.flush()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)

        print(f"OK {w}x{h} seq={seq} epoch={ep} patch_seq={sq}")
        print(f"  {frame_png}")
        if args.show:
            print(f"  {os.path.getsize(frame_png)} B / stride={stride} header_bytes={pixel_off}")
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait(timeout=5)
        for stale in (shm_path,):
            try:
                os.remove(stale)
            except OSError:
                pass


if __name__ == "__main__":
    main()
