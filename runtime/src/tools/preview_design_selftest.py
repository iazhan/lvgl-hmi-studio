#!/usr/bin/env python3
"""chenge-preview-host --design 的冒烟判据（docs/chenge-rt/49 §4 S1）。

设计画布要的是"静止的真图 + 命令驱动重绘"，所以宿主必须满足：

1. 起 `--design` 后 stdout 出 `READY <w> <h>`（编辑器据此建位图，不再轮询魔数）；
2. 帧头 version=2 且 `header_bytes` 等于结构大小（像素起点不再靠两侧各写一个常量）；
3. 首帧已发布：seq >= 1 且画面非全黑；
4. `R` 能重绘一帧（seq 增加）；
5. `L <epoch> <page>` 能重载当前页并回 `A`，`applied_epoch` 更新；
6. `W` 在 S1 阶段被明确拒绝（`E ... not_implemented`），不能让编辑器以为补丁生效了。

用法：
    python3 tools/preview_design_selftest.py [--project 目录] [--data 目录] [-v]
"""
import argparse
import glob
import json
import mmap
import os
import select
import struct
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
RUNTIME = os.path.dirname(HERE)
DEFAULT_PROJECT = "/home/chenge/f1c100s-project/spec/golden/g02_basic_widgets"
DEFAULT_DATA = "/home/chenge/f1c100s-project/logs/scratch/bench-data"

HDR = struct.Struct("<IIIIIIQII")   # magic, ver, w, h, stride, header_bytes, seq, epoch, pseq
PIXEL_OFF = HDR.size


def fail(msg):
    print("FAIL:", msg)
    sys.exit(1)


def start_page_file(project_dir):
    """起始页的 json 文件（J 命令推它）。"""
    start = None
    try:
        with open(os.path.join(project_dir, "manifest.json"), encoding="utf-8") as f:
            start = (json.load(f) or {}).get("start_page")
    except (OSError, ValueError):
        pass
    path = os.path.join(project_dir, "pages", (start or "home") + ".json")
    if os.path.exists(path):
        return path
    files = sorted(glob.glob(os.path.join(project_dir, "pages", "*.json")))
    return files[0] if files else None


def first_widget_id(project_dir):
    """起始页里第一个有 id 的控件（几何补丁的判据要一个真实 id）。

    必须按 manifest.start_page 取页：运行时打开的是它，不是字母序第一个文件
    （按字母序会拿到别的页的控件 id，补丁必然被拒）。
    """
    paths = []
    try:
        with open(os.path.join(project_dir, "manifest.json"), encoding="utf-8") as f:
            start = (json.load(f) or {}).get("start_page")
        if start:
            paths.append(os.path.join(project_dir, "pages", start + ".json"))
    except (OSError, ValueError):
        pass
    paths += sorted(glob.glob(os.path.join(project_dir, "pages", "*.json")))

    for path in paths:
        try:
            with open(path, encoding="utf-8") as f:
                page = json.load(f)
        except (OSError, ValueError):
            continue
        for w in page.get("widgets") or []:
            if isinstance(w, dict) and w.get("id"):
                return w["id"]
    return None


def read_line(proc, timeout=10.0):
    """带超时读一行 stdout；返回 None 表示超时。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        r, _, _ = select.select([proc.stdout], [], [], max(0.0, deadline - time.time()))
        if not r:
            return None
        line = proc.stdout.readline()
        if line:
            return line.strip()
    return None


def read_header(mm):
    return HDR.unpack_from(mm, 0)


def wait_seq(mm, last, timeout=5.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        h = read_header(mm)
        if h[6] != last:
            return h[6]
        time.sleep(0.01)
    return last


def non_black_ratio(mm, w, h, stride):
    hit = 0
    total = 0
    off = PIXEL_OFF
    for y in range(0, h, 17):
        row = off + y * stride
        for x in range(0, w, 23):
            px = mm[row + x * 4: row + x * 4 + 4]
            total += 1
            if px[0] or px[1] or px[2]:
                hit += 1
    return hit / total if total else 0.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--project", default=DEFAULT_PROJECT)
    ap.add_argument("--data", default=DEFAULT_DATA)
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    exe = os.path.join(RUNTIME, "chenge-preview-host")
    if not os.access(exe, os.X_OK):
        fail(f"找不到宿主：{exe}（先 make PREVIEW=1）")

    shm_name = f"/chenge-s1-selftest-{os.getpid()}"
    shm_path = "/dev/shm" + shm_name
    if os.path.exists(shm_path):
        os.unlink(shm_path)

    proc = subprocess.Popen(
        [exe, "--project", args.project, "--data", args.data,
         "--shm", shm_name, "--design"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    try:
        line = read_line(proc, 30.0)
        if line is None:
            err = proc.stderr.read() if proc.stderr else ""
            fail(f"等不到 READY（宿主可能起不来）：{err[-400:]}")
        parts = line.split()
        if parts[0] != "READY" or len(parts) != 3:
            fail(f"第一行应是 READY <w> <h>，实际 {line!r}")
        w, h = int(parts[1]), int(parts[2])
        print(f"OK READY {w}x{h}")

        for _ in range(200):
            if os.path.exists(shm_path):
                break
            time.sleep(0.01)
        with open(shm_path, "rb") as f, mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) as mm:
            magic, ver, hw, hh, stride, hbytes, seq, epoch, pseq = read_header(mm)
            if magic != 0x46474843:
                fail(f"magic 不对：0x{magic:08x}")
            if ver != 2:
                fail(f"version 应为 2，实际 {ver}")
            if (hw, hh) != (w, h):
                fail(f"头里的尺寸 {hw}x{hh} 与 READY {w}x{h} 不一致")
            if hbytes != PIXEL_OFF:
                fail(f"header_bytes 应为 {PIXEL_OFF}，实际 {hbytes}")
            print(f"OK header ver={ver} stride={stride} header_bytes={hbytes}")

            if seq < 1:
                fail(f"设计模式起来后应已发布首帧，seq={seq}")
            ratio = non_black_ratio(mm, w, h, stride)
            if ratio < 0.01:
                fail(f"首帧几乎全黑（非黑比 {ratio:.3%}）")
            print(f"OK first frame seq={seq} 非黑 {ratio:.1%}")

            last = seq
            proc.stdin.write("R\n")
            proc.stdin.flush()
            seq = wait_seq(mm, last)
            if seq == last:
                fail("发 R 之后 seq 没变（render_once 没生效或没发布）")
            print(f"OK R -> seq {last} -> {seq}")

            last = seq
            proc.stdin.write("L 7\n")     # 7 = 新的页面代，page 为空 = 重建当前页
            proc.stdin.flush()
            ack = read_line(proc, 15.0)
            if ack is None or not ack.startswith("A 7"):
                fail(f"L 应回 'A 7 0'，实际 {ack!r}")
            mm.seek(0)
            h2 = read_header(mm)
            if h2[7] != 7:
                fail(f"applied_epoch 应为 7，实际 {h2[7]}")
            if h2[6] <= last:
                fail("L 之后没有新帧")
            print(f"OK L -> {ack} applied_epoch={h2[7]} seq {last} -> {h2[6]}")

            wid = first_widget_id(args.project)
            if wid is None:
                fail("工程里找不到控件 id，几何补丁无法验证")

            # ① 真实控件：几何补丁生效 + 出一帧
            last = read_header(mm)[6]
            proc.stdin.write(f"W 7 5 {wid} 10 20 120 40\n")
            proc.stdin.flush()
            ack = read_line(proc, 10.0)
            if ack is None or not ack.startswith("A 7 5"):
                fail(f"W 应回 'A 7 5'，实际 {ack!r}")
            seq2 = wait_seq(mm, last)
            if seq2 == last:
                fail("W 成功了但没出新帧")
            if read_header(mm)[8] != 5:
                fail(f"applied_seq 应为 5，实际 {read_header(mm)[8]}")
            print(f"OK W 几何补丁 -> {ack} seq {last} -> {seq2}")

            # ② 不存在的控件：必须被拒（客户端据此退整页重载）
            proc.stdin.write("W 7 6 nosuchwidget 0 0 10 10\n")
            proc.stdin.flush()
            rej = read_line(proc, 10.0)
            if rej is None or "patch_rejected" not in rej:
                fail(f"未知控件应回 patch_rejected，实际 {rej!r}")
            print(f"OK W 未知控件 -> {rej}")

            # ③ 推送最新页面 JSON 重建当前页（设计画布停手后的精确刷新）。
            # 这条必须走 J 而不是 L：L 从宿主里的旧快照重读，会把拖走的控件拉回原位。
            page_file = start_page_file(args.project)
            if page_file is None:
                fail("找不到起始页 json，J 命令无法验证")
            with open(page_file, "rb") as f:
                page_bytes = f.read()
            last = read_header(mm)[6]
            # 全走二进制层：TextIOWrapper 与 BufferedWriter 混用会把头与数据拆到两个缓冲里
            proc.stdin.buffer.write(f"J 9 {len(page_bytes)}\n".encode())
            proc.stdin.buffer.write(page_bytes)
            proc.stdin.buffer.write(b"\n")
            proc.stdin.buffer.flush()
            ack = read_line(proc, 20.0)
            if ack is None or not ack.startswith("A 9"):
                # 无应答时把宿主状态吐出来：崩了 / 卡在读 JSON / 解析拒绝，三者处理完全不同
                state = "alive" if proc.poll() is None else f"exited({proc.returncode})"
                tail = ""
                try:
                    r, _, _ = select.select([proc.stderr], [], [], 0.5)
                    if r:
                        tail = os.read(proc.stderr.fileno(), 2000).decode("utf-8", "replace")[-600:]
                except (OSError, ValueError):
                    pass
                fail(f"J 应回 'A 9 0'，实际 {ack!r}；宿主 {state}；stderr: {tail!r}")
            seq3 = wait_seq(mm, last)
            if seq3 == last:
                fail("J 成功了但没出新帧")
            if read_header(mm)[7] != 9:
                fail(f"applied_epoch 应为 9，实际 {read_header(mm)[7]}")
            print(f"OK J 推送 JSON 重建 -> {ack} seq {last} -> {seq3}")

            # ④ 旧页面代：必须被丢弃（整页重载后旧补丁不能落到新页上）
            proc.stdin.write(f"W 3 7 {wid} 0 0 10 10\n")
            proc.stdin.flush()
            stale = read_line(proc, 10.0)
            if stale is None or "stale_epoch" not in stale:
                fail(f"旧 epoch 应回 stale_epoch，实际 {stale!r}")
            print(f"OK W 旧 epoch -> {stale}")

        proc.stdin.write("Q\n")
        proc.stdin.flush()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            fail("Q 之后 5 秒没退出（EOF/退出路径断了）")
        print("PASS preview_host --design 六条判据全过")
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait(timeout=5)
        if os.path.exists(shm_path):
            os.unlink(shm_path)


if __name__ == "__main__":
    main()
