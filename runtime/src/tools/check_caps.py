#!/usr/bin/env python3
"""check_caps.py — 能力表与契约副本的一致性检查

三个地方必须一致，否则编辑器会按"有"给用户画，固件却按"没有"拒载：

  spec/features.json        能力词汇表（编辑器与文档读它）
  src/core/caps.c           运行时编译进去的模块/能力表（--caps 输出它）
  src/project/loader.h      tags/devices/ports/fonts 上限
  src/ui/widgets.h          单页控件上限

用法：python3 tools/check_caps.py
退出码 0 = 一致；1 = 有差异（golden.sh 会先拦这一道）。
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RT = os.path.dirname(HERE)                      # chenge-hmi/chenge-runtime
ROOT = os.path.abspath(os.path.join(RT, "..", ".."))

SPEC = os.path.join(ROOT, "spec", "features.json")
CAPS_C = os.path.join(RT, "src", "core", "caps.c")
CAPS_H = os.path.join(RT, "src", "core", "caps.h")
LOADER_H = os.path.join(RT, "src", "project", "loader.h")
WIDGETS_H = os.path.join(RT, "src", "ui", "widgets.h")
LIMITS_H = os.path.join(RT, "src", "core", "hmi_limits.h")
MAKEFILE = os.path.join(RT, "Makefile")

problems = []


def fail(msg):
    problems.append(msg)


def read(path):
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


def block(text, macro):
    """取 `MACRO(...)` 之后到第一个 `};` 之间的内容。"""
    i = text.find(macro)
    if i < 0:
        return ""
    j = text.find("};", i)
    return text[i:j if j >= 0 else len(text)]


def caps_modules(text):
    """{id: on|None}，None 表示由构建开关决定（HMI_WITH_*）。"""
    out = {}
    for m in re.finditer(r'X\("([a-z0-9_]+)",\s*(HMI_WITH_[A-Z0-9_]+|\d+),', block(text, "HMI_MODULE_LIST(")):
        val = m.group(2)
        out[m.group(1)] = None if val.startswith("HMI_WITH_") else int(val)
    return out


def caps_features(text):
    out = {}
    for m in re.finditer(r'X\("([a-z0-9_]+)",\s*"([a-z0-9_]+)"\)',
                         block(text, "HMI_FEATURE_LIST(")):
        out[m.group(1)] = m.group(2)
    return out


def caps_limits(text):
    out = {}
    for key, macro in (("tags", "CAPS_LIMIT_TAGS"), ("devices", "CAPS_LIMIT_DEVICES"),
                       ("ports", "CAPS_LIMIT_PORTS"), ("fonts", "CAPS_LIMIT_FONTS"),
                       ("widgets_per_page", "CAPS_LIMIT_WIDGETS")):
        if macro == "CAPS_LIMIT_WIDGETS":
            # 间接引用 limits.h 里的构建期开关，值从那里取
            if re.search(r"#define\s+CAPS_LIMIT_WIDGETS\s+HMI_WIDGETS_PER_PAGE", text):
                out[key] = limits_default()
            continue
        m = re.search(r"#define\s+%s\s+(\d+)" % macro, text)
        if m:
            out[key] = int(m.group(1))
    return out


def limits_default():
    """core/limits.h 里 HMI_WIDGETS_PER_PAGE 的默认值（构建期可覆盖）。"""
    m = re.search(r"#define\s+HMI_WIDGETS_PER_PAGE\s+(\d+)", read(LIMITS_H))
    return int(m.group(1)) if m else -1


def c_limits():
    """loader.h / widgets.h 里的真实上限。"""
    out = {}
    pats = {
        "tags": (LOADER_H, r"#define\s+PROJ_TAGS_MAX\s+(\d+)"),
        "devices": (LOADER_H, r"#define\s+PROJ_DEV_MAX\s+(\d+)"),
        "ports": (LOADER_H, r"#define\s+PROJ_PORTS_MAX\s+(\d+)"),
        "fonts": (LOADER_H, r"#define\s+PROJ_FONT_MAX\s+(\d+)"),
        "widgets_per_page": (WIDGETS_H, r"#define\s+WIDGET_MAX_PER_PAGE\s+HMI_WIDGETS_PER_PAGE"),
    }
    for key, (path, pat) in pats.items():
        m = re.search(pat, read(path))
        if not m:
            continue
        # 间接引用：取 limits.h 的默认值
        out[key] = limits_default() if not m.groups() else int(m.group(1))
    return out


def main():
    spec = json.loads(read(SPEC))
    caps_c = read(CAPS_C)
    caps_h = read(CAPS_H)
    mk = read(MAKEFILE)

    spec_mod = spec["modules"]
    spec_feat = spec["features"]
    # 表体在 caps.h（X 宏展开），阈值常量在 caps.c
    mods = caps_modules(caps_h)
    feats = caps_features(caps_h)

    if not mods:
        fail("caps.h 里没解析到模块表（HMI_MODULE_LIST 格式变了？）")
    if not feats:
        fail("caps.h 里没解析到能力表（HMI_FEATURE_LIST 格式变了？）")

    for mid in sorted(set(spec_mod) - set(mods)):
        fail("模块 %s 在 spec/features.json 有、caps.c 没有" % mid)
    for mid in sorted(set(mods) - set(spec_mod)):
        fail("模块 %s 在 caps.c 有、spec/features.json 没有" % mid)
    for mid, on in sorted(mods.items()):
        if mid not in spec_mod:
            continue
        want = spec_mod[mid].get("default")
        sw = spec_mod[mid].get("build")
        if on is None:
            if not sw:
                fail("模块 %s 在 caps.c 由构建开关决定，但 features.json 没写 build" % mid)
            elif not re.search(r"^%s\s*\?=" % re.escape(sw), mk, re.M):
                fail("模块 %s 的构建开关 %s 在 Makefile 里找不到" % (mid, sw))
        elif want is not None and on != want:
            fail("模块 %s：caps.c=%d，features.json default=%d" % (mid, on, want))

    for fid in sorted(set(spec_feat) - set(feats)):
        fail("能力 %s 在 spec/features.json 有、caps.c 没有" % fid)
    for fid in sorted(set(feats) - set(spec_feat)):
        fail("能力 %s 在 caps.c 有、spec/features.json 没有" % fid)
    for fid, mod in sorted(feats.items()):
        if fid in spec_feat and spec_feat[fid]["module"] != mod:
            fail("能力 %s 依赖的模块不一致：caps.c=%s，features.json=%s"
                 % (fid, mod, spec_feat[fid]["module"]))
        if mod not in mods:
            fail("能力 %s 依赖未登记的模块 %s" % (fid, mod))

    # HMI_FEATURE_LIST 的宏注释里不要出现未登记的能力名（防止"文档加了、表没加"）
    for name in re.findall(r"spec/features\.json[^\n]*", caps_h):
        for word in re.findall(r"[a-z][a-z0-9_]{2,}", name):
            if word in spec_feat and word not in feats:
                fail("caps.h 注释里提到能力 %s，但表里没有" % word)

    lim_caps = caps_limits(caps_c)
    lim_real = c_limits()
    for key, val in sorted(lim_real.items()):
        if key not in lim_caps:
            fail("caps.c 缺上限 CAPS_LIMIT_%s" % key.upper())
        elif lim_caps[key] != val:
            fail("上限 %s 不一致：caps.c=%d，代码=%d" % (key, lim_caps[key], val))

    if problems:
        print("!! 能力表不一致（%d 处）：" % len(problems))
        for p in problems:
            print("   - %s" % p)
        return 1

    print("能力表一致：%d 个模块、%d 个能力、%d 项上限"
          % (len(mods), len(feats), len(lim_real)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
