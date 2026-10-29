#!/usr/bin/env python3
"""gen_caps.py — 不跑二进制也能生成与 `chenge-runtime --caps` 相同的 JSON

用途：交叉编译出来的二进制不能在宿主跑，但镜像里要放一份能力表
（/etc/hmi/caps.json，维护页显示与现场排查用）。本脚本从 spec/features.json
与 caps.c 的上限常量推出同一份内容，构建时写进 overlay。

输入与二进制的对应关系：
  模块开关   --with <模块>=0|1（缺省用 spec/features.json 的 default）
  能力清单   features 里模块为 on 的项，顺序同 spec
  上限       caps.c 的 CAPS_LIMIT_*（与 loader.h / widgets.h 的一致性由 check_caps.py 保证）
  版本       --runtime（缺省读 VERSION）
板型           --hw（缺省 f1c100s-kinseal，即 hmi_board.h 的默认分支）

用法：
  python3 tools/gen_caps.py                      打印一行 JSON
  python3 tools/gen_caps.py --hw f1c100s-mcgs --with sim=0 --out caps.json

一致性由 golden.sh 保证：宿主二进制的 --caps 输出必须与本脚本逐字节相同。
"""
import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RT = os.path.dirname(HERE)
REPO = os.path.abspath(os.path.join(RT, "..", ".."))
SPEC = os.path.join(REPO, "spec", "features.json")
CAPS_C = os.path.join(RT, "src", "core", "caps.c")
LIMITS_H = os.path.join(RT, "src", "core", "hmi_limits.h")
VERSION = os.path.join(RT, "VERSION")

LIMIT_KEYS = (("tags", "CAPS_LIMIT_TAGS"), ("devices", "CAPS_LIMIT_DEVICES"),
              ("ports", "CAPS_LIMIT_PORTS"), ("fonts", "CAPS_LIMIT_FONTS"),
              ("widgets_per_page", "CAPS_LIMIT_WIDGETS"))


def wpp_default():
    """每页控件上限的默认值：core/hmi_limits.h 的 HMI_WIDGETS_PER_PAGE。"""
    text = open(LIMITS_H, encoding="utf-8").read()
    m = re.search(r"#define\s+HMI_WIDGETS_PER_PAGE\s+(\d+)", text)
    if not m:
        raise SystemExit("hmi_limits.h 里找不到 HMI_WIDGETS_PER_PAGE")
    return int(m.group(1))


def limits(wpp):
    text = open(CAPS_C, encoding="utf-8").read()
    out = {}
    for key, macro in LIMIT_KEYS:
        if key == "widgets_per_page":
            # 间接引用 hmi_limits.h 的构建期开关；命令行给了就以它为准（与 make 同源）
            if not re.search(r"#define\s+CAPS_LIMIT_WIDGETS\s+HMI_WIDGETS_PER_PAGE", text):
                raise SystemExit("caps.c 里 CAPS_LIMIT_WIDGETS 应引用 HMI_WIDGETS_PER_PAGE")
            out[key] = wpp if wpp is not None else wpp_default()
            continue
        m = re.search(r"#define\s+%s\s+(\d+)" % macro, text)
        if not m:
            raise SystemExit("caps.c 里找不到 %s" % macro)
        out[key] = int(m.group(1))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hw", default="f1c100s-kinseal", help="hw_profile（默认 kinseal）")
    ap.add_argument("--widgets-per-page", type=int, default=None,
                    help="每页控件上限（默认取 core/hmi_limits.h 的值；与 make WIDGETS_PER_PAGE 保持一致）")
    ap.add_argument("--runtime", default=None, help="运行时版本（默认读 VERSION）")
    ap.add_argument("--with", dest="withs", action="append", default=[],
                    metavar="模块=0|1", help="覆盖模块编入状态，可多次给")
    ap.add_argument("--out", default=None, help="写入文件（默认打印到 stdout）")
    a = ap.parse_args()

    spec = json.load(open(SPEC, encoding="utf-8"))
    ver = a.runtime
    if not ver:
        ver = open(VERSION, encoding="utf-8").read().strip()

    override = {}
    for item in a.withs:
        name, _, val = item.partition("=")
        if name not in spec["modules"]:
            raise SystemExit("未知模块 %s（spec/features.json 里没有）" % name)
        override[name] = 1 if val.strip() not in ("0", "false", "no") else 0

    mods = {}
    for mid, m in spec["modules"].items():
        mods[mid] = bool(override.get(mid, m.get("default", 0)))

    feats = [fid for fid, f in spec["features"].items() if mods.get(f["module"])]

    out = {
        "runtime": ver,
        "hw_profile": a.hw,
        "modules": mods,
        "features": feats,
        "limits": limits(a.widgets_per_page),
    }
    line = json.dumps(out, ensure_ascii=False, separators=(",", ":")) + "\n"

    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            f.write(line)
    else:
        sys.stdout.write(line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
