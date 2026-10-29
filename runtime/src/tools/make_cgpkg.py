#!/usr/bin/env python3
"""把 37 格式的工程目录打成 .cgpkg（37 §13：64 字节小端头 + ustar 载荷，V1 不压缩）。

为什么宿主侧要有一份独立实现：板端解包（chenge-common/src/hmi_project_import.c）
与编辑器导出（ChengePkgWriter.cs）是两套语言写的，谁写歪了都得靠第三方读一遍才发现。
这里按文档逐字节实现，`tools/pkg_selftest.sh` 拿它的产物喂板端解包代码 ——
两条实现都对齐文档，才谈得上互通。

用法：
    python3 tools/make_cgpkg.py <工程目录> <输出.cgpkg> [--variant NAME]

变体（只用于负向判据）：
    bad_magic    magic 改成 XXXX
    bad_crc      载荷翻一个字节（包头 CRC 仍写原值）
    bad_path     加一条 ../evil.txt
    bad_hw       hw_profile 写成别的板
    bad_len      payload_size 多算 1 字节
    truncated    砍掉最后 100 字节
"""
import argparse
import io
import json
import os
import struct
import sys
import tarfile
import zlib

MAGIC = b"HMI1"
HEADER_SIZE = 64
HEADER_VERSION = 1
FLAG_SIGNED, FLAG_CRYPT, FLAG_ZIP = 0x1, 0x2, 0x4
PAYLOAD_MAX = 32 * 1024 * 1024


def read_manifest(project_dir):
    p = os.path.join(project_dir, "manifest.json")
    with open(p, "rb") as f:
        text = f.read().decode("utf-8")
    m = json.loads(text)
    fv = str(m.get("format_version", ""))
    major, _, minor = fv.partition(".")
    return int(major or 0), int(minor or 0), str(m.get("hw_profile", "")), str(m.get("name", ""))


def build_ustar(project_dir, extra_entries=()):
    """标准 ustar。mtime/uid/gid 固定，保证同一份工程每次打出的字节相同。"""
    buf = io.BytesIO()
    files = []
    for root, dirs, names in os.walk(project_dir):
        dirs.sort()
        for name in sorted(names):
            full = os.path.join(root, name)
            rel = os.path.relpath(full, project_dir).replace(os.sep, "/")
            files.append((rel, full))

    with tarfile.open(fileobj=buf, mode="w", format=tarfile.USTAR_FORMAT) as tar:
        for rel, full in sorted(files):
            with open(full, "rb") as f:
                data = f.read()
            info = tarfile.TarInfo(rel)
            info.size = len(data)
            info.mtime = 0
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            info.mode = 0o644
            tar.addfile(info, io.BytesIO(data))

        for rel, data in extra_entries:
            info = tarfile.TarInfo(rel)
            info.size = len(data)
            info.mtime = 0
            info.uid = info.gid = 0
            info.mode = 0o644
            tar.addfile(info, io.BytesIO(data))

    return buf.getvalue()


def make_header(major, minor, payload, hw_profile, build_time=0, flags=0):
    payload_crc = zlib.crc32(payload) & 0xFFFFFFFF
    body = struct.pack(
        "<4sHHHHIIII16s16s",
        MAGIC, HEADER_SIZE, HEADER_VERSION, major, minor, flags,
        len(payload) & 0xFFFFFFFF, payload_crc, build_time & 0xFFFFFFFF,
        hw_profile.encode("ascii")[:16].ljust(16, b"\0"),
        b"\0" * 16,
    )
    assert len(body) == HEADER_SIZE - 4, len(body)
    return body + struct.pack("<I", zlib.crc32(body) & 0xFFFFFFFF)


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("project_dir")
    ap.add_argument("out")
    ap.add_argument("--variant", default="")
    args = ap.parse_args(argv)

    major, minor, hw, name = read_manifest(args.project_dir)
    extra = []
    if args.variant == "bad_path":
        extra.append(("../evil.txt", b"x"))

    payload = build_ustar(args.project_dir, extra)
    if len(payload) > PAYLOAD_MAX:
        print(f"载荷 {len(payload)} 字节超过 32MB 上限", file=sys.stderr)
        return 2

    header = make_header(major, minor, payload, hw)

    if args.variant == "bad_magic":
        header = b"XXXX" + header[4:]
        header = header[:60] + struct.pack("<I", zlib.crc32(header[:60]) & 0xFFFFFFFF)
    elif args.variant == "bad_hw":
        header = make_header(major, minor, payload, "f1c100s-mcgs")
    elif args.variant == "bad_len":
        header = make_header(major, minor, payload, hw)
        size = struct.unpack_from("<I", header, 16)[0]
        header = header[:16] + struct.pack("<I", size + 1) + header[20:]
        header = header[:60] + struct.pack("<I", zlib.crc32(header[:60]) & 0xFFFFFFFF)
    elif args.variant == "bad_crc":
        # 翻的必须是**文件数据**里的字节：落在头部会让 tar 校验和先报错，
        # 落在 10KB 记录的零填充里会被"尾部有多余数据"先拦住 ——
        # 两种都测不到载荷 CRC 这条判据本身。
        payload = bytearray(payload)
        anchor = bytes(range(16))
        at = payload.find(anchor)
        if at < 0:
            print("bad_crc: 找不到资产数据锚点（夹具变了？）", file=sys.stderr)
            return 2
        payload[at] ^= 0xFF
        payload = bytes(payload)
    elif args.variant in ("", "truncated", "bad_path"):
        pass
    else:
        print(f"未知变体 {args.variant}", file=sys.stderr)
        return 2

    blob = header + payload
    if args.variant == "truncated":
        blob = blob[:-100]

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "wb") as f:
        f.write(blob)
    print(f"{args.out}: {len(blob)} 字节（载荷 {len(payload)}，工程 {name}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
