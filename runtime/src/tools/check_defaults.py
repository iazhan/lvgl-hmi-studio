#!/usr/bin/env python3
"""缺省值一致性：契约 `default` 必须等于运行时「键缺失时用的值」。

为什么需要这条判据：`spec/widget-schema.json` 的 `default` 是「不写这个键就得到什么」的
**唯一对外承诺**（手写工程、其它工具、NovaHMI 导入都只看它）。运行时各 build_* 里的
`hmi_json_int(hmi_json_get(props, "k"), N)` 的 N 就是它实际兑现的值。两边不一致时，
用编辑器做出来的控件（键全写）没问题，换一种方式产出的工程就悄悄换了个样子 ——
这类差异不会报错、不会整页拒绝，只会「看起来不一样」。

扫的是 build_* 里**直接**取 props 的回落值：
  - `hmi_json_get(props, "k")`  —— 控件属性
  - gauge 的细节键在 `jaw/jtl/jnw/jcr` 这几个显式句柄上取（同一层，同一个 props）
共用辅助函数里的回落值（`apply_text_style` 的颜色/字号、`apply_text_valign` 的对齐）
不在这里判：它们一处定义、多类共用，谁是契约值要看调用方，判据是像素级的
`tools/defaults_probe.py`。

用法：python3 tools/check_defaults.py [--verbose]
退出码 1 = 有差异（golden.sh 会先跑它）。
"""

import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RT = os.path.dirname(HERE)
ROOT = os.path.abspath(os.path.join(RT, "..", ".."))
SPEC = os.path.join(ROOT, "spec", "widget-schema.json")
UI = os.path.join(RT, "src", "ui")

# build_* → 契约里的控件类型名（widgets.c 的建页分派表）
FN2WIDGET = {
    "build_button": "button", "build_switch": "switch", "build_checkbox": "checkbox",
    "build_keyboard": "keyboard", "build_tabview": "tabview", "build_slider": "slider",
    "build_label": "label", "build_value": "value", "build_enum_text": "enum_text",
    "build_ascii_display": "ascii_display", "build_message": "message",
    "build_input": "input", "build_combobox": "combobox", "build_roller": "roller",
    "build_lamp": "lamp", "build_knob": "knob", "build_spinner": "spinner",
    "build_bar": "bar", "build_gauge": "gauge", "build_pipe": "pipe",
    "build_image": "image", "build_state_image": "state_image", "build_qrcode": "qrcode",
    "build_frame_anim": "frame_anim", "build_table": "table", "build_curve": "history_curve",
    "build_realtime_curve": "realtime_curve", "build_xy_curve": "xy_curve",
    "build_history_table": "history_table", "build_chart": "chart",
    "build_alarm_bar": "alarm_bar", "build_alarm_list": "alarm_list",
    "build_recipe_table": "recipe_table",
}

# 只有这几个变量名是「控件 props 本身」（其余是 confirm / pressed / cells / source 等子对象）
PROPS_HANDLES = {"props", "jaw", "jtl", "jnw", "jcr"}

CALL = re.compile(
    r'hmi_json_(int|num|str|bool)\(\s*hmi_json_get\(\s*([a-z_]+)\s*,\s*"([a-z_0-9]+)"\s*\)\s*,\s*([^,\)]+?)\s*\)')
# 先把 `const hmi_json_t* jcr = hmi_json_get(props, "center_radius")` 这类句柄记下来，
# 再扫 `hmi_json_int(jcr, 5)`（gauge 的细节键都走这个写法）。
HANDLE = re.compile(
    r'const\s+hmi_json_t\s*\*\s*(\w+)\s*=\s*hmi_json_get\(\s*([a-z_]+)\s*,\s*"([a-z_0-9]+)"\s*\)')
DIRECT = re.compile(r'hmi_json_(int|num|str|bool)\(\s*([a-z_]+)\s*,\s*([^,\)]+?)\s*\)')

# 读得到、但契约里没有的键：不算缺省差异，单独列为「契约缺键」
EXTRA_OK = {
    ("bar", "orientation"): "v1 早期的方向别名，导出器只写 direction",
    ("input", "unit"): "输入框单位，契约里没有这个键（docs/chenge-rt/50 §6.6 已登记）",
    ("input", "step"): "契约有 step、两侧都不读",
}

# 契约里 default 是 null（= 无缺省）的键不比对数值：它们的语义是「键在不在决定做不做」，
# 运行时天然没有一个统一数 —— radius/border_width 缺键时吃控件自带外观（按钮 13、
# 输入框 4+1px），input.min/max 缺键时那一侧不校验。这些只能像素级判。


def parse_c(path):
    """[(func, key, kind, fallback_raw, lineno)]"""
    out = []
    cur = None
    handles = {}
    for lineno, line in enumerate(open(path, encoding="utf-8", errors="replace"), 1):
        m = re.match(r"\s*(?:static\s+)?(?:int|void)\s+(\w+)\s*\(", line)
        if m:
            cur = m.group(1)
            handles = {}
        for h in HANDLE.finditer(line):
            if h.group(2) in PROPS_HANDLES:
                handles[h.group(1)] = h.group(3)
        for c in CALL.finditer(line):
            if c.group(2) in PROPS_HANDLES:
                out.append((cur, c.group(3), c.group(1), c.group(4).strip(), lineno))
        for d in DIRECT.finditer(line):
            if d.group(2) in handles:
                out.append((cur, handles[d.group(2)], d.group(1), d.group(3).strip(), lineno))
    return out


def same(contract, runtime):
    if isinstance(contract, bool) or isinstance(runtime, bool):
        return contract is runtime or contract == runtime
    if isinstance(contract, (int, float)) and isinstance(runtime, (int, float)):
        return abs(float(contract) - float(runtime)) < 1e-9
    return contract == runtime


def main():
    verbose = "--verbose" in sys.argv
    spec = json.load(open(SPEC, encoding="utf-8"))
    common = spec["common"]
    diffs, extras, checked, null_defaults = [], [], 0, []

    for name in sorted(os.listdir(UI)):
        if not name.startswith("widget_") or not name.endswith(".c"):
            continue
        for func, key, kind, raw, lineno in parse_c(os.path.join(UI, name)):
            widget = FN2WIDGET.get(func)
            if widget is None:
                continue
            props = spec["widgets"].get(widget, {}).get("props", {})
            where = "%s:%d" % (name, lineno)

            if key in props:
                contract = props[key].get("default")
            elif key in common:
                contract = common[key].get("default")
            else:
                if (widget, key) not in EXTRA_OK:
                    extras.append((widget, key, where, raw))
                continue

            try:
                runtime = {"int": int, "num": float,
                           "bool": lambda s: bool(int(s))}.get(kind, json.loads)(raw)
            except (ValueError, TypeError):
                continue      # 回落值是表达式（如 min(ww,hh)），判据在像素探针

            if contract is None:
                null_defaults.append((widget, key, where))
                continue

            checked += 1
            if not same(contract, runtime):
                diffs.append((widget, key, contract, runtime, where))

    print("缺省一致性：扫描 %d 条直接回落值（契约 props + common）" % checked)
    for widget, key, contract, runtime, where in diffs:
        print("  ✗ %-16s %-14s 契约 %-12s 运行时 %-12s  %s" % (
            widget, key, json.dumps(contract, ensure_ascii=False),
            json.dumps(runtime, ensure_ascii=False), where))
    for widget, key, where in null_defaults:
        print("  · %-16s %-14s 契约 default=null（按键存在性判定，不比数值，%s）" % (widget, key, where))
    for widget, key, where, raw in extras:
        print("  · %-16s %-14s 运行时读了、契约里没有（回落 %s，%s）" % (widget, key, raw, where))
    if verbose:
        print("  （--verbose：%d 条一致）" % (checked - len(diffs)))

    if diffs:
        print("\n!! %d 条缺省与契约不一致：要么改运行时回落值，要么改 spec 的 default" % len(diffs))
        return 1
    print("ok：全部一致")
    return 0


if __name__ == "__main__":
    sys.exit(main())
