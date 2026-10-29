#!/usr/bin/env python3
"""控件外观探针：同一页、同一类控件逐个改外观属性，量真机位图变了没有。

两种用法：

1. `--matrix`：对「控件类型 × 外观属性」逐项渲染（属性 A 值 vs B 值），打印控件矩形内
   变化的像素数与两块矩形的主色。这就是"面板上能配"与"位图里生效"的差距表；改运行时
   前后各跑一次，看哪些行的 changed 从 0 变成非 0。
2. `--multi`：同一页放同一类型的 N 个实例，各给一个不同的颜色，断言每个实例矩形内的
   主色就是自己那个颜色 —— "多个实例逐个修改、位图随之改变"的判据。按类型共用一个
   样式的实现会在这里红。

为什么不用画布截图：本脚本跑 `chenge-runtime`（板端同源产物）的 `HMI_SHOT`，量的是
SDL 渲染缓冲里那 800×480 像素；画布走 `chenge-preview-host`，同源码不同产物
（见 docs/chenge-rt/51）。

用法：
    python3 tools/appearance_probe.py --matrix [--filter label.]
    python3 tools/appearance_probe.py --multi
"""

import argparse
import json
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RT = os.path.dirname(HERE)
ROOT = os.path.abspath(os.path.join(RT, "..", ".."))
BIN = os.path.join(RT, "chenge-runtime")
SCHEMA = os.path.join(ROOT, "spec", "widget-schema.json")

# 落在仓库 logs/ 下（约定：产物不进 /tmp、不进仓库根）
LOG_ROOT = os.path.join(ROOT, "logs", "appearance")

BG = "#101820"          # 页面底色：与控件外观颜色都要有明显差别，便于取色
BASE = {"x": 40, "y": 60, "w": 200, "h": 80}


def widget_requires(wtype):
    """控件声明的工程能力（manifest.features）。缺了运行时整个工程拒绝加载。"""
    try:
        with open(SCHEMA, encoding="utf-8") as f:
            w = json.load(f)["widgets"][wtype]
    except (OSError, KeyError):
        return None
    return w.get("requires")


# 一个变量：tag0 = 60（量程 0..100 的 60%）。指示器类控件要有值才有可见的填充段；
# 开关的 checked 也绑它（tagdb_bool 对非 0 数值为真）。取值由 sim.json 的常量生成器
# 写入，不靠时间推进 —— 同一份工程每次渲染的像素必须一致。
TAGS = [
    {"id": 0, "name": "数值", "type": "i16", "access": "r", "min": 0, "max": 100,
     "source": {"device": "sim1", "area": "holding", "addr": 100, "datatype": "i16"}},
]
DEVICES = [{"id": "sim1", "name": "模拟设备", "protocol": "sim"}]
SIM = {
    "tick_ms": 100,
    "gens": [
        {"area": "holding", "addr": 100, "datatype": "i16", "init": 60,
         "step": 0, "min": 0, "max": 100},
    ],
}


def write_project(dirpath, widgets, page_bg=BG, sim_init=60):
    feats = []
    for w in widgets:
        f = widget_requires(w["type"])
        if f and f not in feats:
            feats.append(f)
    manifest = {
        "format_version": "0.6",
        "min_runtime": "0.32.0",
        "hw_profile": "f1c100s-kinseal",
        "name": "appearance_probe",
        "version": "1.0.0",
        "screen": {"width": 800, "height": 480},
        "start_page": "home",
        "default_language": "zh",
        "features": feats,
        "font_sizes": [16, 20],
        "counts": {"tags": len(TAGS), "pages": 1, "devices": len(DEVICES), "scripts": 0},
    }
    os.makedirs(os.path.join(dirpath, "pages"), exist_ok=True)
    with open(os.path.join(dirpath, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    # 三个顶层文件缺一个就"工程加载失败"（loader 不静默跳过），探针工程也要给全
    for name, body in (("ports.json", []), ("devices.json", DEVICES),
                       ("tags.json", TAGS), ("sim.json", SIM)):
        with open(os.path.join(dirpath, name), "w", encoding="utf-8") as f:
            json.dump(body, f, ensure_ascii=False)
    if sim_init != SIM["gens"][0]["init"]:
        # 绑定值要能改：进度弧/填充段这类判据比的是"同一个变量取两个值"，
        # 而矩阵只改 props。改写 sim.json 的常量生成器初值即可（不靠时间推进）。
        sim = json.loads(json.dumps(SIM))
        sim["gens"][0]["init"] = sim_init
        with open(os.path.join(dirpath, "sim.json"), "w", encoding="utf-8") as f:
            json.dump(sim, f, ensure_ascii=False)
    page = {"id": "home", "title": "appearance", "bg_color": page_bg, "widgets": widgets}
    with open(os.path.join(dirpath, "pages", "home.json"), "w", encoding="utf-8") as f:
        json.dump(page, f, ensure_ascii=False, indent=2)


def render(widgets, tag, page_bg=BG, data_seed=None, out_root=None, env_extra=None,
           sim_init=60):
    """渲染一页，返回 (Bmp, 运行时日志)。data_seed(dir) 用于给表/曲线备数据文件。

    out_root 换输出根（默认 logs/appearance）；defaults_probe.py 用 logs/defaults。
    env_extra 追加环境变量（如 HMI_FONT_DIR：不指定时宿主没有 TTF，所有字号都
    回落到内嵌的 14/16 子集，"改字号位图不动"就成了假判据）。
    sim_init 改绑定变量（tag 0）的初值：比"同一个变量取两个值"时用。
    """
    out = os.path.join(out_root or LOG_ROOT, tag)
    os.makedirs(out, exist_ok=True)
    proj = os.path.join(out, "proj")
    write_project(proj, widgets, page_bg, sim_init)
    data = os.path.join(out, "data")
    os.makedirs(data, exist_ok=True)
    if data_seed:
        data_seed(data)
    shot = os.path.join(out, "shot.bmp")
    if os.path.exists(shot):
        os.remove(shot)
    env = dict(os.environ)
    env.update({
        "SDL_VIDEODRIVER": "dummy",
        "HMI_PROJECT_DIR": proj,
        "HMI_DATA_DIR": data,
        "HMI_SHOT": shot,
        "HMI_RT_TRACE": "1",
    })
    if env_extra:
        env.update(env_extra)
    p = subprocess.run([BIN], env=env, capture_output=True, text=True, timeout=60)
    log = (p.stdout or "") + (p.stderr or "")
    if not os.path.exists(shot):
        raise SystemExit(f"渲染失败（{tag}）：没有截图\n{log[-2000:]}")
    return Bmp(shot), log


class Bmp:
    def __init__(self, path):
        data = open(path, "rb").read()
        self.offset = struct.unpack_from("<I", data, 10)[0]
        self.width = struct.unpack_from("<i", data, 18)[0]
        self.height = abs(struct.unpack_from("<i", data, 22)[0])
        self.bpp = struct.unpack_from("<H", data, 28)[0]
        self.px = self.bpp // 8
        self.row = (self.width * self.px + 3) & ~3
        self.data = data

    def rgb(self, x, y):
        """屏幕坐标（左上原点）→ (r, g, b)。BMP 行序自下而上。"""
        row = self.height - 1 - y
        p = self.offset + row * self.row + x * self.px
        b, g, r = self.data[p], self.data[p + 1], self.data[p + 2]
        return (r, g, b)

    def hex(self, x, y):
        return "#%02X%02X%02X" % self.rgb(x, y)

    def rect_diff(self, other, x, y, w, h):
        n = 0
        for yy in range(y, y + h):
            for xx in range(x, x + w):
                if self.rgb(xx, yy) != other.rgb(xx, yy):
                    n += 1
        return n

    def colors(self, x, y, w, h):
        s = {}
        for yy in range(y, y + h):
            for xx in range(x, x + w):
                c = self.rgb(xx, yy)
                s[c] = s.get(c, 0) + 1
        return s

    def dominant(self, x, y, w, h):
        """矩形内出现最多的颜色。

        不用中心点取色：按钮/输入框的中心落在文字笔画上，采到的是字形反锯齿与底色的
        混色（实测 #FF0000 按钮的中心是 #FF3333），会误判"底色不对"。
        """
        s = self.colors(x, y, w, h)
        return "#%02X%02X%02X" % max(s.items(), key=lambda kv: kv[1])[0]


def widget(wid, wtype, props, rect=None, bind=None):
    r = dict(BASE)
    r.update(rect or {})
    w = {"id": wid, "type": wtype, "x": r["x"], "y": r["y"], "w": r["w"], "h": r["h"],
         "props": props}
    if bind:
        w["bind"] = bind
    return w


# ── 探针矩阵 ──────────────────────────────────────────────
# 每项：类型、基础 props、要探的属性、两个取值；可选 bind（指示器类要给一个 60%
# 的值，否则填充段宽度为 0，改颜色也看不出像素差）。
# 两个取值刻意选成视觉上必然不同的颜色/尺寸，否则"像素差"说明不了问题。
# border_color 必须配 border_width，否则边框宽 0、改色看不见。
V60 = {"value": {"tag": 0}}          # 数值 60（见 TAGS / SIM）
ON = {"checked": {"tag": 0}}         # 开关接通（非 0 即通）

CASES = [
    # label：三种基础外观 + 文字
    {"t": "label", "p": {"text": "Abc 1", "font_size": 20, "color": "#FFFFFF"}, "k": "bg_color", "v": ["#C02020", "#20C020"]},
    {"t": "label", "p": {"text": "Abc 1", "font_size": 20, "color": "#FFFFFF"}, "k": "border_width", "v": [6, 0]},
    {"t": "label", "p": {"text": "Abc 1", "font_size": 20, "color": "#FFFFFF", "border_width": 6}, "k": "border_color", "v": ["#FF0000", "#0000FF"]},
    {"t": "label", "p": {"text": "Abc 1", "font_size": 20, "color": "#FFFFFF", "bg_color": "#203040"}, "k": "radius", "v": [40, 0]},
    {"t": "label", "p": {"text": "Abc 1", "font_size": 20, "color": "#FFFFFF"}, "k": "opacity", "v": [60, 255]},
    {"t": "label", "p": {"text": "Abc 1", "font_size": 20}, "k": "color", "v": ["#FF0000", "#0000FF"]},
    {"t": "label", "p": {"text": "Abc 1", "color": "#FFFFFF"}, "k": "font_size", "v": [12, 32]},

    # button
    {"t": "button", "p": {"text": "Btn", "font_size": 20, "color": "#FFFFFF", "bg_color": "#3060C0"}, "k": "radius", "v": [40, 0]},
    {"t": "button", "p": {"text": "Btn", "font_size": 20, "color": "#FFFFFF"}, "k": "bg_color", "v": ["#C02020", "#20C020"]},
    {"t": "button", "p": {"text": "Btn", "font_size": 20, "color": "#FFFFFF", "border_width": 6}, "k": "border_color", "v": ["#FF0000", "#0000FF"]},
    {"t": "button", "p": {"text": "Btn", "font_size": 20, "color": "#FFFFFF"}, "k": "opacity", "v": [60, 255]},

    # switch：color_on/color_off 是自带键；bg_color 只当 color_off 的兜底
    {"t": "switch", "p": {"color_on": "#20C020", "color_off": "#404040"}, "k": "color_on", "v": ["#20C020", "#C02020"], "bind": ON},
    {"t": "switch", "p": {"color_on": "#20C020", "color_off": "#404040"}, "k": "color_off", "v": ["#404040", "#C02020"]},
    {"t": "switch", "p": {"color_on": "#20C020"}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},
    {"t": "switch", "p": {"color_on": "#20C020", "color_off": "#404040"}, "k": "opacity", "v": [60, 255]},

    # lamp：灯珠色就是 color，没有独立底色（面板也不给这一类开「背景色」）
    {"t": "lamp", "p": {"color": "#20C020", "size": 40}, "k": "color", "v": ["#20C020", "#C02020"]},
    {"t": "lamp", "p": {"color": "#20C020", "size": 40}, "k": "opacity", "v": [60, 255]},

    # 指示器族：填充段要有值才看得见
    {"t": "spinner", "p": {"color": "#20C020"}, "k": "color", "v": ["#20C020", "#C02020"]},
    {"t": "spinner", "p": {"color": "#20C020"}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},
    {"t": "gauge", "p": {"min": 0, "max": 100, "color": "#20C020", "ticks": 5}, "k": "color", "v": ["#20C020", "#C02020"], "bind": V60},
    {"t": "gauge", "p": {"min": 0, "max": 100, "color": "#20C020", "ticks": 5}, "k": "bg_color", "v": ["#C02020", "#2020C0"], "bind": V60},
    {"t": "knob", "p": {"min": 0, "max": 100, "color": "#20C020"}, "k": "color", "v": ["#20C020", "#C02020"], "bind": V60},
    {"t": "knob", "p": {"min": 0, "max": 100, "color": "#20C020"}, "k": "bg_color", "v": ["#C02020", "#2020C0"], "bind": V60},

    # bar / slider
    {"t": "bar", "p": {"min": 0, "max": 100, "color": "#20C020"}, "k": "color", "v": ["#20C020", "#C02020"], "bind": V60},
    {"t": "bar", "p": {"min": 0, "max": 100, "color": "#20C020"}, "k": "bg_color", "v": ["#C02020", "#2020C0"], "bind": V60},
    {"t": "bar", "p": {"min": 0, "max": 100, "color": "#20C020"}, "k": "direction", "v": ["horizontal", "vertical"], "bind": V60},
    {"t": "slider", "p": {"min": 0, "max": 100, "color": "#20C020"}, "k": "bg_color", "v": ["#C02020", "#2020C0"], "bind": V60},
    {"t": "slider", "p": {"min": 0, "max": 100, "color": "#20C020"}, "k": "opacity", "v": [60, 255], "bind": V60},

    # 输入族
    {"t": "checkbox", "p": {"text": "Chk", "font_size": 20, "color": "#FFFFFF"}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},
    {"t": "checkbox", "p": {"text": "Chk", "font_size": 20, "color": "#FFFFFF", "box_color": "#20C020"}, "k": "box_color", "v": ["#20C020", "#C02020"]},
    {"t": "input", "p": {"mode": "text", "font_size": 20}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},
    {"t": "input", "p": {"mode": "text", "font_size": 20}, "k": "radius", "v": [20, 0]},
    {"t": "input", "p": {"mode": "text", "font_size": 20}, "k": "border_width", "v": [6, 0]},
    {"t": "input", "p": {"mode": "text", "font_size": 20}, "k": "border_color", "v": ["#FF0000", "#0000FF"]},
    {"t": "combobox", "p": {"options": ["A", "B"], "font_size": 20}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},
    {"t": "combobox", "p": {"options": ["A", "B"], "font_size": 20}, "k": "border_width", "v": [6, 0]},
    {"t": "roller", "p": {"options": ["A", "B", "C"], "visible_rows": 3, "font_size": 20}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},

    # 文本族
    {"t": "value", "p": {"decimals": 1, "font_size": 20, "color": "#FFFFFF"}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},
    {"t": "enum_text", "p": {"text": "Run", "font_size": 20, "color": "#FFFFFF"}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},
    {"t": "ascii_display", "p": {"encoding": "utf8", "max_len": 8, "font_size": 20, "color": "#FFFFFF"}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},
    {"t": "message", "p": {"text": "Hi", "level": "info"}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},
    {"t": "message", "p": {"text": "Hi", "level": "info"}, "k": "radius", "v": [20, 0]},

    # 数据区（table 的 cells 是 {r,c,text} 对象数组）
    {"t": "table", "p": {"rows": 2, "cols": 2, "font_size": 16,
                         "cells": [{"r": 0, "c": 0, "text": "a"}, {"r": 1, "c": 1, "text": "d"}]},
     "k": "bg_color", "v": ["#C02020", "#2020C0"]},
    {"t": "table", "p": {"rows": 2, "cols": 2, "font_size": 16,
                         "cells": [{"r": 0, "c": 0, "text": "a"}]}, "k": "border_width", "v": [6, 0]},
    {"t": "tabview", "p": {"tabs": ["P1", "P2"], "tab_pos": "top", "tab_h": 32}, "k": "bg_color", "v": ["#C02020", "#2020C0"]},

    # 图元族（fill_color / border_color / radius / fill_opa）
    {"t": "shape", "p": {"kind": "rect"}, "k": "fill_color", "v": ["#C02020", "#20C020"]},
    {"t": "shape", "p": {"kind": "rect", "border_width": 6}, "k": "border_color", "v": ["#FF0000", "#0000FF"]},
    {"t": "shape", "p": {"kind": "rect", "fill_color": "#203040"}, "k": "radius", "v": [30, 0]},
    {"t": "shape", "p": {"kind": "rect", "fill_color": "#C02020"}, "k": "fill_opa", "v": [80, 255]},

    # ── 「面板上有这一行、运行时自己写死」的键 ────────────────
    # 这几项的共同点：导出器一直把键写出去，面板也一直显示行，缺的是运行时读取。
    # 判据只问一件事：改了这个键，控件矩形里的像素变没变。
    {"t": "combobox", "p": {"options": ["AAAA", "BBBB"], "font_size": 12, "color": "#FFFFFF"},
     "k": "font_size", "v": [12, 28]},
    {"t": "combobox", "p": {"options": ["AAAA", "BBBB"], "font_size": 20},
     "k": "color", "v": ["#FF0000", "#0000FF"]},
    # 输入框对齐：空输入框没有字，位图不会变；绑一个变量才有可对齐的内容
    # （输入框的绑定属性名是 text，不是 value —— 写 value 时运行时只走 BIND_VALUE，
    #  输入框不显示任何文字，测出来的 0 像素差是假的）
    {"t": "input", "p": {"mode": "num", "font_size": 20}, "k": "align", "v": ["left", "right"],
     "bind": {"text": {"tag": 0}}},
    {"t": "gauge", "p": {"min": 0, "max": 100, "color": "#20C020"}, "k": "show_value", "v": [True, False], "bind": V60},
    {"t": "gauge", "p": {"min": 0, "max": 100, "color": "#20C020"}, "k": "label_pos", "v": ["inside", "none"], "bind": V60},
    # 圆弧是描边图元：面板上的「描边色」必须画出来（运行时此前只读 fill_color）
    {"t": "shape", "p": {"kind": "arc", "border_width": 8}, "k": "border_color", "v": ["#FF0000", "#0000FF"]},

    # 本轮新补的契约键：面板上本来就有行，契约里也定了，但此前既没写也没读
    {"t": "lamp", "p": {"color": "#20C020", "size": 40}, "k": "brightness", "v": [40, 255]},
    # 文字必须同时超出框宽与框高：DOTS 只在"排不下最后一行"时才加省略号，
    # 只超宽时它和 wrap 画出来一模一样（实测过，会误判成"没生效"）
    {"t": "label", "p": {"text": "0123456789" * 12, "font_size": 20, "color": "#FFFFFF"},
     "k": "long_mode", "v": ["wrap", "dots"]},
    {"t": "combobox", "p": {"options": ["AAAA", "BBBB", "CCCC"], "font_size": 20},
     "k": "selected", "v": [0, 2]},
    # 管道流向：两块在同一个相位上的位置互为镜像（判据只比像素，不看动画时序）
    {"t": "pipe", "p": {"direction": "h", "wall": 0.2, "color": "#C02020"},
     "k": "flow_dir", "v": ["forward", "reverse"]},
    # 按点画的管道：管径必须改得动位图（不写 points 的直管按框取管径，width 对它无效 —— 别拿那条测）
    {"t": "pipe", "p": {"points": [[10, 70], [100, 70], [100, 10]], "wall": 0.2,
                        "color": "#C02020", "width": 8},
     "k": "width", "v": [8, 30]},
]


def run_matrix(filt=None, verbose=True):
    if not os.path.exists(BIN):
        raise SystemExit(f"找不到 {BIN}，先在 chenge-runtime 里 make HOST=1")
    os.makedirs(LOG_ROOT, exist_ok=True)
    if verbose:
        print(f"{'控件':<12}{'属性':<14}{'A→B':<26}{'变化像素':>9}  {'A 主色':<9}{'B 主色':<9}")
        print("-" * 80)
    rows = []
    for c in CASES:
        wtype, key, vals = c["t"], c["k"], c["v"]
        if filt and filt not in f"{wtype}.{key}":
            continue
        shots = []
        for j, val in enumerate(vals):
            p = dict(c["p"])
            p[key] = val
            shots.append(render([widget("w1", wtype, p, bind=c.get("bind"))],
                                f"matrix/{wtype}_{key}_{j}")[0])
        a, b = shots
        x, y, w, h = BASE["x"], BASE["y"], BASE["w"], BASE["h"]
        changed = a.rect_diff(b, x, y, w, h)
        if verbose:
            print(f"{wtype:<12}{key:<14}{str(vals[0]) + '→' + str(vals[1]):<26}{changed:>9}  "
                  f"{a.dominant(x, y, w, h):<9}{b.dominant(x, y, w, h):<9}")
        rows.append((wtype, key, changed))
    zero = [f"{t}.{k}" for t, k, n in rows if n == 0]
    if verbose:
        print("-" * 80)
        print(f"{len(rows)} 项，{len(zero)} 项位图完全没变：")
        for z in zero:
            print(f"  {z}")
    return rows, zero


# ── 多实例判据 ────────────────────────────────────────────
# 同一页、同一类型、多个实例，各给一个不同的颜色；断言每个实例矩形里的主色就是自己
# 那个颜色。共享样式 / 只读第一个实例的实现在这里红。
MULTI = [
    {"t": "label", "p": {"text": "Abc", "font_size": 20}, "k": "bg_color"},
    {"t": "button", "p": {"text": "B", "font_size": 20, "color": "#FFFFFF"}, "k": "bg_color"},
    # 开关的旋钮是白的、几乎占满高度，取色要避开它：取轨道右侧 70% 处（断开态 = 轨道色）
    {"t": "switch", "p": {"color_on": "#00FF00"}, "k": "color_off",
     "rect": {"w": 160, "h": 48}, "at": (112, 24)},
    {"t": "checkbox", "p": {"text": "C", "font_size": 20, "color": "#FFFFFF"}, "k": "bg_color"},
    {"t": "input", "p": {"mode": "text", "font_size": 20}, "k": "bg_color"},
    {"t": "table", "p": {"rows": 2, "cols": 2, "font_size": 16,
                         "cells": [{"r": 0, "c": 0, "text": "a"}]}, "k": "bg_color"},
    {"t": "gauge", "p": {"min": 0, "max": 100, "ticks": 5}, "k": "bg_color", "bind": V60},
    {"t": "shape", "p": {"kind": "rect"}, "k": "fill_color"},
]


# ── 「填充段跟着圆角走」判据 ────────────────────────────────
# 滑块/进度条的可见填充段是 INDICATOR part：只把 radius 刷到 MAIN（轨道）的话，
# 填充段自己仍是主题给的圆头/直角 —— 用户在面板上改圆角，看到的"那条"不跟着变。
# 判法取填充段的**左端**（右端被滑块旋钮盖住）：radius=0 时四角的位置必须是填充色，
# radius 大于半径时同一点必须被圆角吃掉。只作用到 MAIN 的实现会在 radius=0 这一半红。
EDGE = [
    {"t": "slider", "p": {"min": 0, "max": 100, "color": "#C02020", "bg_color": "#203040"}},
    {"t": "bar", "p": {"min": 0, "max": 100, "color": "#C02020", "bg_color": "#203040"}},
]


def run_edge():
    bad = []
    rect = {"x": 40, "y": 60, "w": 240, "h": 90}
    for c in EDGE:
        wtype, fill = c["t"], c["p"]["color"].upper()
        square, _ = render([widget("w1", wtype, dict(c["p"], radius=0), rect, bind=V60)],
                           f"edge/{wtype}_0")
        round_, _ = render([widget("w1", wtype, dict(c["p"], radius=24), rect, bind=V60)],
                           f"edge/{wtype}_24")
        probes = [(rect["x"] + 2, rect["y"] + 1), (rect["x"] + 2, rect["y"] + rect["h"] - 2)]
        ok = True
        for px, py in probes:
            sq, rd = square.hex(px, py).upper(), round_.hex(px, py).upper()
            if sq != fill:
                bad.append(f"{wtype}: radius=0 时 ({px},{py}) 是 {sq}，应为 {fill}"
                           "（填充段没吃到圆角 0，还是自己的圆头）")
                ok = False
            if rd == fill:
                bad.append(f"{wtype}: radius=24 时 ({px},{py}) 仍是 {rd}，圆角没作用到填充段")
                ok = False
        print(f"{wtype}: 填充段左端 " + ("ok" if ok else "FAIL"))
    for m in bad:
        print("  " + m)
    return bad


# ── 灯珠形状判据 ──────────────────────────────────────────
# 设计器拖拽走的是"只改 lv_obj 尺寸"的几何快路径（DesignHostPreview 的 W 补丁）。
# 灯珠自己当控件对象时，把框拖成长方形的那一瞬间它被拉成椭圆，停手整页重建才变回圆。
# 判法：宽框（200×90）里画出来，亮色区的包围盒必须是正方形、且位于框中心。
def run_lamp():
    def lit(rect, tag):
        bmp, _ = render([widget("w1", "lamp", {"color": "#38C172"}, rect)], f"lamp/{tag}")
        xs, ys = [], []
        for yy in range(rect["y"], rect["y"] + rect["h"]):
            for xx in range(rect["x"], rect["x"] + rect["w"]):
                r, g, _ = bmp.rgb(xx, yy)
                if g > 120 and r < 120:
                    xs.append(xx)
                    ys.append(yy)
        if not xs:
            return None, bmp
        return (max(xs) - min(xs) + 1, max(ys) - min(ys) + 1,
                (min(xs) + max(xs)) / 2 - (rect["x"] + rect["w"] / 2),
                (min(ys) + max(ys)) / 2 - (rect["y"] + rect["h"] / 2)), bmp

    # 同一个灯（不给 size = 按框取小者）：正方形框 vs 宽框，灯珠本身必须一模一样。
    # 用"两次渲染对比"而不是"宽=高"，是为了绕开 lv_led 的光晕边（亮区比名义直径宽几像素），
    # 那几像素是固定量，两图相减就抵消了；真被拉成椭圆时差的是几十像素。
    square, _ = lit({"x": 40, "y": 60, "w": 90, "h": 90}, "square_box")
    wide, wide_bmp = lit({"x": 40, "y": 60, "w": 200, "h": 90}, "wide_box")
    if square is None or wide is None:
        print("  lamp: 没有亮色像素（灯没画出来）")
        return ["lamp: 没有亮色像素"]

    sw, sh, scx, scy = square
    ww, wh, wcx, wcy = wide
    print(f"lamp: 方框里 {sw}x{sh}（偏心 {scx:+.0f},{scy:+.0f}）；"
          f"宽框里 {ww}x{wh}（偏心 {wcx:+.0f},{wcy:+.0f}）")
    bad = []
    # 容差 8px：lv_led 的自带光晕会往容器里多画几像素，窄框里被父容器裁掉、宽框里漏出来，
    # 实测差 4px。真被拉成椭圆时差的是"框宽 - 直径"（几十像素），不是这个量级。
    if abs(ww - sw) > 8 or abs(wh - sh) > 8:
        bad.append(f"lamp: 宽框里灯珠变成 {ww}x{wh}（方框里是 {sw}x{sh}）—— 形状必须与框比例无关")
    if abs(wcx - scx) > 3 or abs(wcy - scy) > 3:
        bad.append(f"lamp: 宽框里灯珠偏离中心（偏心 {wcx:+.0f},{wcy:+.0f}，方框里 {scx:+.0f},{scy:+.0f}）")

    # ① 灯珠直径 = 短边 - 2×光晕（光晕要完整落在框内，观感才不打折）
    # ② 光晕不许被框裁：同一颗灯珠放到足够大的框里（不可能被裁）与放在紧贴的框里，
    #    球心周围那一块必须逐像素相同。
    rect = {"x": 40, "y": 60, "w": 120, "h": 120}
    ball = min(rect["w"], rect["h"]) - 16          # 2 × LAMP_GLOW(8)
    bmp, _ = render([widget("w1", "lamp", {"color": "#38C172"}, rect)], "lamp/round")

    explicit, _ = render([widget("w1", "lamp", {"color": "#38C172", "size": ball}, rect)],
                         "lamp/explicit_ball")
    auto_diff = bmp.rect_diff(explicit, rect["x"] - 40, rect["y"] - 40,
                              rect["w"] + 80, rect["h"] + 80)
    print(f"lamp: 不写 size 与显式 size={ball} 差 {auto_diff} 像素")
    if auto_diff != 0:
        bad.append(f"lamp: 不写 size 与显式 {ball}px（短边-2×光晕）差了 {auto_diff} 像素")

    # 光晕观感判据：同样 104px 的灯珠，放进 200×200 的大框（光晕绝无可能被裁）作参照
    big = {"x": 40, "y": 60, "w": 200, "h": 200}
    ref, _ = render([widget("w1", "lamp", {"color": "#38C172", "size": ball}, big)], "lamp/glow_ref")
    half = ball // 2 + 10                          # 球心外 10px 已含整圈光晕
    cx1, cy1 = rect["x"] + rect["w"] // 2, rect["y"] + rect["h"] // 2
    cx2, cy2 = big["x"] + big["w"] // 2, big["y"] + big["h"] // 2
    halo_diff = 0
    for dy in range(-half, half + 1):
        for dx in range(-half, half + 1):
            if bmp.hex(cx1 + dx, cy1 + dy).upper() != ref.hex(cx2 + dx, cy2 + dy).upper():
                halo_diff += 1
    print(f"lamp: 紧贴框与宽框的光晕差异 {halo_diff} 像素（{half * 2 + 1}px 方块内）")
    if halo_diff > 0:
        bad.append(f"lamp: 紧贴框里的光晕与宽框不一致（{halo_diff} 像素）—— 光晕被框裁掉了")

    for m in bad:
        print("  " + m)
    return bad


def run_gauge():
    """指针式仪表必须"弧照旧 + 多一根指针"，不能把弧改窄/改色。"""
    color = "#2F70D0"
    rect = {"x": 40, "y": 60, "w": 200, "h": 200}
    bind = {"value": {"tag": 0}}          # tag0 = 60
    # 量程取 0..120 ⇒ 值 60 正好半程，指针朝正上方（y 方向），取样点才不会随角度漂
    common = {"min": 0, "max": 120, "color": color, "arc_width": 8}
    arc, _ = render([widget("w1", "gauge", dict(common, style="arc"), rect, bind=bind)], "gauge/arc")
    ptr, _ = render([widget("w1", "gauge", dict(common, style="pointer"), rect, bind=bind)], "gauge/pointer")

    cx, cy = rect["x"] + rect["w"] // 2, rect["y"] + rect["h"] // 2
    bad = []

    # 弧的左端（lv_arc 把弧画在对象边界上，左端就是矩形左边内 4px）
    left = (rect["x"] + 4, cy)
    a_hex, p_hex = arc.hex(*left).upper(), ptr.hex(*left).upper()
    print(f"gauge: 弧左端 {a_hex}（arc）/ {p_hex}（pointer），指针位 {ptr.hex(cx, cy - 40).upper()}")
    if p_hex != color.upper():
        bad.append(f"gauge: 指针式把弧改样了 —— 弧左端是 {p_hex}，应为仪表色 {color.upper()}")
    if a_hex != color.upper():
        bad.append(f"gauge: 弧式本身的弧左端是 {a_hex}，应为 {color.upper()}")

    # 弧的**粗细**必须与样式无关：沿 y=cy 从左边往里数连续的仪表色像素
    def band(bmp):
        n = 0
        for x in range(rect["x"], rect["x"] + 40):
            if bmp.hex(x, cy).upper() == color.upper():
                n += 1
            elif n:
                break
        return n

    ba, bp = band(arc), band(ptr)
    print(f"gauge: 弧宽 arc={ba}px pointer={bp}px，指针位 {ptr.hex(cx, cy - 40).upper()}")
    if abs(ba - bp) > 1:
        bad.append(f"gauge: 勾上指针后弧粗细变了（arc {ba}px / pointer {bp}px）")

    # 指针位置上：只有 pointer 才有那根线
    needle = ptr.hex(cx, cy - 40).upper()
    if needle != color.upper():
        bad.append(f"gauge: 指针位置（{cx},{cy - 40}）是 {needle}，没画出指针")

    # 刻度线位置：勾不勾指针，刻度必须在同一个圈上、同样长。
    # 判法：取刻度色（#4A5A6A）像素的包围盒，两种样式必须一致（±1px）。
    def tick_box(style):
        img, _ = render([widget("w1", "gauge", dict(common, style=style, ticks=6, tick_len=6), rect, bind=bind)],
                        f"gauge/ticks_{style}")
        xs, ys = [], []
        for yy in range(rect["y"], rect["y"] + rect["h"]):
            for xx in range(rect["x"], rect["x"] + rect["w"]):
                if img.hex(xx, yy).upper() == "#4A5A6A":
                    xs.append(xx)
                    ys.append(yy)
        return (min(xs), min(ys), max(xs), max(ys)) if xs else None

    ta, tp = tick_box("arc"), tick_box("pointer")
    print(f"gauge: 刻度包围盒 arc={ta} pointer={tp}")
    if ta is None or tp is None:
        bad.append("gauge: 没画出刻度线（ticks=6 时应有一圈刻度）")
    elif max(abs(ta[i] - tp[i]) for i in range(4)) > 1:
        bad.append(f"gauge: 勾上指针后刻度位置变了（arc {ta} / pointer {tp}）")

    # 背景：勾不勾指针，表盘内部的颜色必须**一样**。
    # 不配底色 ⇒ 两种样式都透出页面底色；配了底色 ⇒ 两种样式都画那个底色。
    # （指针式的根必须是控件盒，不能是一根 line —— 把底色刷在 line 上就是"背景变了"。）
    for bg, want in ((None, BG.upper()), ("#FF0000", "#FF0000")):
        for name, style in (("arc", "arc"), ("pointer", "pointer")):
            props = dict(common, style=style)
            if bg:
                props["bg_color"] = bg
            img, _ = render([widget("w1", "gauge", props, rect, bind=bind)], f"gauge/bg_{style}_{bg or 'none'}")
            px = img.hex(cx - 40, cy - 40).upper()
            if px != want:
                bad.append(f"gauge: 底色={bg or '未配'} 时 {name} 样式表盘内部是 {px}，应为 {want}")

    for m in bad:
        print("  " + m)
    return bad


def run_multi():
    bad = []
    colors = ["#FF0000", "#00FF00", "#0000FF"]
    for c in MULTI:
        wtype, key = c["t"], c["k"]
        widgets, rects = [], []
        for i, col in enumerate(colors):
            p = dict(c["p"])
            p[key] = col
            r = {"x": 20 + i * 250, "y": 120, "w": 220, "h": 180}
            r.update(c.get("rect") or {})
            widgets.append(widget(f"w{i}", wtype, p, r, bind=c.get("bind")))
            rects.append(r)
        bmp, _ = render(widgets, f"multi/{wtype}")
        nbad = 0
        for i, col in enumerate(colors):
            r = rects[i]
            if c.get("at"):
                got = bmp.hex(r["x"] + c["at"][0], r["y"] + c["at"][1])
            else:
                got = bmp.dominant(r["x"], r["y"], r["w"], r["h"])
            if got != col.upper():
                bad.append(f"{wtype} 实例 {i}: 取色 {got}，期望 {col.upper()}")
                nbad += 1
        print(f"{wtype}: {len(colors)} 个实例 " + ("ok" if nbad == 0 else "FAIL"))
    for m in bad:
        print("  " + m)
    return bad


# ── 图元几何与进度弧判据 ──────────────────────────────────
# 三条都问同一个问题："改了这个值，位图变没变"：
#   polyline.points ：折线换一组点集（>0 像素差）
#   arc.angle       ：弧换起止角（>0 像素差）
#   arc.value       ：弧绑一个变量，值变 → 进度段长度变；**没绑定时一个像素都不动**
# 最后一条是"老工程观感不变"的正面判据：未绑定时进度段必须 0 像素，
# 否则一段越画越长的进度弧会悄悄出现在所有历史工程的图上。
FIG_RECT = {"x": 60, "y": 60, "w": 240, "h": 240}
RING = "#00FF00"        # arc 的背景环（border_color）
PROG = "#FF0000"        # arc 的进度段（fill_color）
POLY = "#FF0000"


def _color_pixels(bmp, which):
    """控件矩形里纯色像素数（r=红 / g=绿）。antialias 边缘不算，只看实心段。"""
    n = 0
    for yy in range(FIG_RECT["y"], FIG_RECT["y"] + FIG_RECT["h"]):
        for xx in range(FIG_RECT["x"], FIG_RECT["x"] + FIG_RECT["w"]):
            r, g, b = bmp.rgb(xx, yy)
            if which == "r" and r > 200 and g < 80 and b < 80:
                n += 1
            elif which == "g" and g > 200 and r < 80 and b < 80:
                n += 1
    return n


def _prog_pixels(bmp):
    return _color_pixels(bmp, "r")


def _ring_pixels(bmp):
    return _color_pixels(bmp, "g")


def _diff(a, b):
    return a.rect_diff(b, FIG_RECT["x"], FIG_RECT["y"], FIG_RECT["w"], FIG_RECT["h"])


def run_figures():
    bad = []
    os.makedirs(LOG_ROOT, exist_ok=True)

    # ① polyline.points：两组明显不同的点集（对角线 vs 折角）
    for name, pa, pb in (("polyline.points",
                          [[0, 0], [FIG_RECT["w"] - 1, FIG_RECT["h"] - 1]],
                          [[0, FIG_RECT["h"] - 1], [FIG_RECT["w"] - 1, 0]]),):
        a, _ = render([widget("w1", "shape", {"kind": "polyline", "points": pa,
                                              "border_color": POLY, "border_width": 8}, FIG_RECT)],
                      "figures/polyline_a")
        b, _ = render([widget("w1", "shape", {"kind": "polyline", "points": pb,
                                              "border_color": POLY, "border_width": 8}, FIG_RECT)],
                      "figures/polyline_b")
        n = _diff(a, b)
        print(f"{name:<18}{n:>9} 像素差")
        if n == 0:
            bad.append(f"{name}: 两组点集画出同一张图（点集根本没进渲染）")

    # ② arc.angle：起止角各改一次（分开测，证明两个键都读）
    def arc_bmp(tag, **props):
        p = {"kind": "arc", "border_color": RING, "border_width": 10}
        p.update(props)
        return render([widget("w1", "shape", p, FIG_RECT)], f"figures/{tag}")[0]

    a0_set = [
        ("arc.angle.start", {"angle_start": 0, "angle_end": 360}, {"angle_start": 200, "angle_end": 360}),
        ("arc.angle.end", {"angle_start": 0, "angle_end": 360}, {"angle_start": 0, "angle_end": 120}),
        ("arc.angle", {"angle_start": 0, "angle_end": 120}, {"angle_start": 120, "angle_end": 300}),
    ]
    for name, pa, pb in a0_set:
        a = arc_bmp("angle_a_" + name.replace(".", "_"), **pa)
        b = arc_bmp("angle_b_" + name.replace(".", "_"), **pb)
        n = _diff(a, b)
        print(f"{name:<18}{n:>9} 像素差")
        if n == 0:
            bad.append(f"{name}: 改了起止角位图不变（角度没进渲染）")

    # ③ arc.value：同一张图，未绑定 → 绑定 0 → 绑定 80 → 绑定 100
    arc_props = {"kind": "arc", "border_color": RING, "border_width": 10,
                 "fill_color": PROG, "min": 0, "max": 100}
    unbound, _ = render([widget("w1", "shape", dict(arc_props), FIG_RECT)], "figures/arc_unbound")
    v0, _ = render([widget("w1", "shape", dict(arc_props), FIG_RECT, bind={"value": {"tag": 0}})],
                   "figures/arc_v0", sim_init=0)
    v80, _ = render([widget("w1", "shape", dict(arc_props), FIG_RECT, bind={"value": {"tag": 0}})],
                    "figures/arc_v80", sim_init=80)
    v100, _ = render([widget("w1", "shape", dict(arc_props), FIG_RECT, bind={"value": {"tag": 0}})],
                     "figures/arc_v100", sim_init=100)

    p_un, p0, p80, p100 = (_prog_pixels(unbound), _prog_pixels(v0),
                           _prog_pixels(v80), _prog_pixels(v100))
    print(f"{'arc.value':<18}未绑定 {p_un} 段像素 / 值0 {p0} / 值80 {p80} / 值100 {p100}")
    if p_un != 0:
        bad.append(f"arc.value: 没绑 value 却画出了 {p_un} 个进度像素 —— 老工程观感必须不变")
    if p0 != 0:
        bad.append(f"arc.value: 值=量程下限仍画出 {p0} 个进度像素（LVGL 里起止角相等是整圈）")
    if p80 == 0:
        bad.append("arc.value: 绑了 value=80 却没有进度像素（进度弧没实现）")
    if p100 < p80:
        bad.append(f"arc.value: 值 100 的进度段（{p100}）比值 80（{p80}）还短")
    if _diff(unbound, v0) != 0:
        bad.append("arc.value: 值=下限的位图与未绑定不同（应当完全一样）")
    n = _diff(v0, v80)
    print(f"{'arc.value 0→80':<18}{n:>9} 像素差")
    if n == 0:
        bad.append("arc.value: 值 0 与 80 画出同一张图")

    # ④ 总量守恒：进度段 + 背景环 ≈ 整圈。只比"变了没变"会漏掉"进度段画到环外面"
    # （弧宽/内缩量传错时两者加起来会明显多出一圈）。整圈的基准就取未绑定那张图。
    ring_full = _ring_pixels(unbound)
    full = _prog_pixels(v100) + _ring_pixels(v100)
    print(f"{'arc.value 满量程':<18}进度 {_prog_pixels(v100)} + 环 {_ring_pixels(v100)}"
          f" = {full}（整圈 {ring_full}）")
    if ring_full <= 0:
        bad.append("arc.value: 未绑定的弧没画出背景环（判据基准缺失）")
    elif abs(full - ring_full) > ring_full * 0.15:
        bad.append(f"arc.value: 满量程时进度段与环合计 {full} 像素，应接近整圈 {ring_full}")

    # ⑤ 负起始角 / 跨 0 的弧（契约允许 -360..720）：扫掠必须按"终角 - 起角"算。
    # lv_draw_sw_mask 把 <0 的角**夹到 0**（不是加 360），所以 -210..30 这种 240° 弧
    # 会被画成 30°；规范化必须发生在建控件处，这里同时守背景环与进度段。
    neg = dict(arc_props, angle_start=-210, angle_end=30)
    neg_bmp, _ = render([widget("w1", "shape", dict(neg), FIG_RECT)], "figures/arc_neg_unbound")
    neg_ring = _ring_pixels(neg_bmp)
    want_ring = ring_full * 240.0 / 360.0
    print(f"{'arc.angle 负起始角':<18}环 {neg_ring} 像素（240° 应 ≈{want_ring:.0f}）")
    if abs(neg_ring - want_ring) > want_ring * 0.12:
        bad.append(f"arc.angle: -210..30 的背景环是 {neg_ring} 像素，应接近 {want_ring:.0f}"
                   f"（<0 的角被夹到 0 就会画成 30°）")

    neg_counts = []
    for pct in (10, 50, 90):
        img, _ = render([widget("w1", "shape", dict(neg), FIG_RECT, bind={"value": {"tag": 0}})],
                        f"figures/arc_neg_{pct}", sim_init=pct)
        neg_counts.append(_prog_pixels(img))
    print(f"{'arc.value 负角':<18}10% {neg_counts[0]} / 50% {neg_counts[1]} / 90% {neg_counts[2]} 段像素")
    if not (0 < neg_counts[0] < neg_counts[1] < neg_counts[2]):
        bad.append(f"arc.value: 负起始角的进度段不随值单调增长（10/50/90 → {neg_counts}）")

    for m in bad:
        print("  " + m)
    return bad


# ── 绑定层判据 ────────────────────────────────────────────
# 这两条对应 docs/chenge-rt/50 §8 里"契约允许、运行时没分支"补上的两处：
#   checkbox.bind.text    → 勾选框的文字随变量走
#   combobox.bind.selected → 选中项随变量走
# 判法：同一个控件，变量取两个值（sim 常量生成器初值），位图必须不同。
def run_binds():
    bad = []
    rect = {"x": 40, "y": 60, "w": 240, "h": 60}

    # fmt 必须用整型族：tag 0 是 i16，`%.0f` 会被建页期拒绝（整页建不起来 ⇒ 两次都是空页）
    cb = {"text": "勾选", "font_size": 20, "color": "#FFFFFF"}
    a, _ = render([widget("w1", "checkbox", dict(cb), rect,
                          bind={"text": {"tag": 0, "kind": "fmt", "fmt": "%d"}})],
                  "binds/checkbox_text_10", sim_init=10)
    b, _ = render([widget("w1", "checkbox", dict(cb), rect,
                          bind={"text": {"tag": 0, "kind": "fmt", "fmt": "%d"}})],
                  "binds/checkbox_text_90", sim_init=90)
    n = a.rect_diff(b, rect["x"], rect["y"], rect["w"], rect["h"])
    print(f"{'checkbox.text':<18}{n:>9} 像素差")
    if n == 0:
        bad.append("checkbox.text: 绑了变量但文字不跟着变（运行时没有 checkbox 的 text 分支）")

    combo = {"options": ["AAAA", "BBBB", "CCCC"], "font_size": 20}
    a, _ = render([widget("w1", "combobox", dict(combo), rect, bind={"selected": {"tag": 0}})],
                  "binds/combobox_selected_0", sim_init=0)
    b, _ = render([widget("w1", "combobox", dict(combo), rect, bind={"selected": {"tag": 0}})],
                  "binds/combobox_selected_2", sim_init=2)
    n = a.rect_diff(b, rect["x"], rect["y"], rect["w"], rect["h"])
    print(f"{'combobox.selected':<18}{n:>9} 像素差")
    if n == 0:
        bad.append("combobox.selected: 绑了变量但选中项不跟着变（运行时没有 selected 分支）")

    # 输入框的 value 绑定：导出器对数值输入框发的就是 value，运行时必须落到 textarea 上
    inp = {"mode": "num", "font_size": 20}
    a, _ = render([widget("w1", "input", dict(inp), rect, bind={"value": {"tag": 0}})],
                  "binds/input_value_10", sim_init=10)
    b, _ = render([widget("w1", "input", dict(inp), rect, bind={"value": {"tag": 0}})],
                  "binds/input_value_90", sim_init=90)
    n = a.rect_diff(b, rect["x"], rect["y"], rect["w"], rect["h"])
    print(f"{'input.value':<18}{n:>9} 像素差")
    if n == 0:
        bad.append("input.value: 绑了变量但输入框内容不动（value 该走 textarea 那条通路）")

    for m in bad:
        print("  " + m)
    return bad


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--matrix", action="store_true")
    ap.add_argument("--multi", action="store_true")
    ap.add_argument("--edge", action="store_true")
    ap.add_argument("--lamp", action="store_true")
    ap.add_argument("--gauge", action="store_true")
    ap.add_argument("--figures", action="store_true")
    ap.add_argument("--binds", action="store_true")
    ap.add_argument("--filter", default=None)
    args = ap.parse_args()
    if args.matrix:
        _, zero = run_matrix(args.filter)
        return 1 if zero else 0
    if args.multi:
        return 1 if run_multi() else 0
    if args.edge:
        return 1 if run_edge() else 0
    if args.lamp:
        return 1 if run_lamp() else 0
    if args.gauge:
        return 1 if run_gauge() else 0
    if args.figures:
        return 1 if run_figures() else 0
    if args.binds:
        return 1 if run_binds() else 0
    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
