#!/usr/bin/env python3
"""gen_gbk_table.py — 生成 GBK→Unicode 映射表（src/data/gbk_table.c）

ADR-0007：Tag DB 存设备原始字节，`ascii_display` 渲染时按 encoding 转 UTF-8；
GBK 是双字节（部分单字节 ASCII），所以需要一张「前导字节 × 后续字节 → Unicode」表。

表结构（两级线性索引，O(1) 查找）：
  idx = (lead - 0x81) * 191 + (trail - 0x40)
  0 表示该组合在 GBK 里没有对应字符。
191 = 0x40..0xFE 的个数；0x7F 那一列恒为 0（GBK 不用它）。

用法：
  python3 tools/gen_gbk_table.py            # 生成
  python3 tools/gen_gbk_table.py --check    # 只校验生成物是否与当前表一致
"""
import sys
import os

LEAD_MIN, LEAD_MAX = 0x81, 0xFE
TRAIL_MIN, TRAIL_MAX = 0x40, 0xFE
TRAIL_COUNT = TRAIL_MAX - TRAIL_MIN + 1          # 191
LEAD_COUNT = LEAD_MAX - LEAD_MIN + 1             # 126

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT_C = os.path.join(ROOT, "src/data/gbk_table.c")
OUT_H = os.path.join(ROOT, "src/data/gbk_table.h")

HEADER_C = """/*
 * gbk_table.c — GBK → Unicode 映射表（生成物，勿手改）
 *
 * 由 tools/gen_gbk_table.py 生成；改了生成脚本再重新跑一遍。
 * 索引：idx = (lead - 0x%02X) * %d + (trail - 0x%02X)，0 = 该组合无映射。
 */
#include "gbk_table.h"

const uint16_t gbk_uni_map[GBK_MAP_SIZE] = {
""" % (LEAD_MIN, TRAIL_COUNT, TRAIL_MIN)

HEADER_H = """/*
 * gbk_table.h — GBK 映射表的形状（生成物，勿手改，见 tools/gen_gbk_table.py）
 */
#ifndef HMI_GBK_TABLE_H
#define HMI_GBK_TABLE_H

#include <stdint.h>

#define GBK_LEAD_MIN  0x%02X
#define GBK_LEAD_MAX  0x%02X
#define GBK_TRAIL_MIN 0x%02X
#define GBK_TRAIL_MAX 0x%02X
#define GBK_TRAIL_COUNT %d
#define GBK_MAP_SIZE   %d

/* 0 = 该组合在 GBK 里没有字符；非 0 为 Unicode 码位（BMP，可直接转 3 字节 UTF-8） */
extern const uint16_t gbk_uni_map[GBK_MAP_SIZE];

#endif /* HMI_GBK_TABLE_H */
""" % (LEAD_MIN, LEAD_MAX, TRAIL_MIN, TRAIL_MAX, TRAIL_COUNT,
       LEAD_COUNT * TRAIL_COUNT)


def build_table():
    table = [0] * (LEAD_COUNT * TRAIL_COUNT)
    mapped = 0
    for lead in range(LEAD_MIN, LEAD_MAX + 1):
        base = (lead - LEAD_MIN) * TRAIL_COUNT
        for trail in range(TRAIL_MIN, TRAIL_MAX + 1):
            try:
                ch = bytes((lead, trail)).decode("gbk")
            except UnicodeDecodeError:
                continue
            if len(ch) != 1:
                continue
            cp = ord(ch)
            if cp > 0xFFFF:          # 只收 BMP，超出的按无映射处理
                continue
            table[base + trail - TRAIL_MIN] = cp
            mapped += 1
    return table, mapped


def render_c(table):
    lines = [HEADER_C]
    for i in range(0, len(table), 12):
        chunk = table[i:i + 12]
        lines.append("    " + " ".join("0x%04X," % v for v in chunk) + "\n")
    lines.append("};\n")
    return "".join(lines)


def main():
    table, mapped = build_table()
    c_src = render_c(table)
    if "--check" in sys.argv:
        old = open(OUT_C, encoding="utf-8").read() if os.path.exists(OUT_C) else ""
        if old != c_src:
            print("!! gbk_table.c 与生成脚本不一致，先跑 python3 tools/gen_gbk_table.py")
            return 1
        print("gbk 表与生成物一致（%d 条映射）" % mapped)
        return 0
    os.makedirs(os.path.dirname(OUT_C), exist_ok=True)
    with open(OUT_C, "w", encoding="utf-8") as f:
        f.write(c_src)
    if not os.path.exists(OUT_H):
        with open(OUT_H, "w", encoding="utf-8") as f:
            f.write(HEADER_H)
    print("已生成 %s（%d 条映射，%.1f KB）"
          % (OUT_C, mapped, len(c_src) / 1024.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
