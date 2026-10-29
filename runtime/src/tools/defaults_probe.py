#!/usr/bin/env python3
"""缺省值探针：**不写这个键** 与 **写出契约缺省值** 画出来必须一模一样。

判据形状（每条 CASE 三个渲染）：

  A = 只给「控件能建出来」的最小 props（要探的键缺席）
  B = A + {键: 契约 default}            → 位图必须与 A 完全相同（0 像素差）
  C = A + {键: 一个明显不同的值}          → 必须与 A 不同（证明这条判据真的看得见）

只有 A≡B 同时 A≉C 才算过：前者说明运行时缺键时兑现了契约的 `default`，后者说明这条
判据不是「文字没画出来所以怎么改都是 0」。

为什么静态判据（`tools/check_defaults.py`）不够：有些缺省不在 build_* 里，而是落在
共用辅助函数（`apply_text_style` 的颜色/字号被 label / value / enum_text 共用），
还有些缺省是**语义**而不是数字 —— 表缺 rows/cols 时应当按 3×3 建页（而不是整页拒绝），
灯缺 size 时应当"按控件宽高取小者"。这些只能看位图。

用法：python3 tools/defaults_probe.py [--filter label]
退出码 1 = 有不一致（golden.sh 会跑它）。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import appearance_probe as ap          # noqa: E402  （复用工程写入与位图读取）

OUT_ROOT = os.path.join(ap.ROOT, "logs", "defaults")
RECT = dict(ap.BASE)                   # 40,60,200,80

# 板端那套 TTF：不给的话宿主没有字体文件，所有字号都回落到内嵌的 14/16 子集，
# 「缺键 16 / 显式 20」会画成同一张图，字号判据就成了假绿。
FONT_DIR = os.path.join(ap.ROOT, "f1c100s-os", "chenge-bsp", "board", "common",
                        "rootfs-overlay", "usr", "lib", "hmi", "fonts")
ENV = {"HMI_FONT_DIR": FONT_DIR} if os.path.isdir(FONT_DIR) else {}

# 读数类控件要绑定一个值才有字：tag0 = 60（见 appearance_probe.TAGS / SIM）
BIND_TEXT = {"text": {"tag": 0}}
BIND_ON = {"checked": {"tag": 0}}      # 开关接通

# why：这条缺省归哪一侧所有（写给人看，也是取舍的依据）
CASES = [
    # ── 文字类：缺省色/字号落在共用辅助函数里，静态判据看不见 ──
    dict(t="label", p={"text": "Abc 123"}, k="color", v="#E6EDF3", wrong="#FF0000",
         why="apply_text_style 的缺省色：契约 #E6EDF3（与主题文字色同一支）"),
    # 字号只能取工程里声明过的档（探针 manifest 给的是 16/20），否则 FreeType 回落，
    # 判据会变成"怎么改都不动"
    dict(t="label", p={"text": "Abc 123", "color": "#E6EDF3"}, k="font_size", v=16, wrong=20,
         why="契约 label.font_size=16"),
    dict(t="enum_text", p={"text": "Abc 123"}, k="color", v="#E6EDF3", wrong="#FF0000",
         why="同 apply_text_style"),
    dict(t="value", p={"color": "#E6EDF3", "decimals": 0}, k="font_size", v=20, wrong=16,
         bind=BIND_TEXT, why="契约 value.font_size=20（读数比正文大一号）"),
    dict(t="value", p={"color": "#E6EDF3", "font_size": 20}, k="decimals", v=0, wrong=5,
         bind=BIND_TEXT, why="契约 value.decimals=0"),
    dict(t="value", p={"decimals": 0, "font_size": 20}, k="color", v="#E6EDF3", wrong="#FF0000",
         bind=BIND_TEXT, why="同 apply_text_style"),

    # ── 指示器 / 灯 / 仪表 ──
    dict(t="switch", p={"color_off": "#404040"}, k="color_on", v="#38C172", wrong="#FF0000",
         bind=BIND_ON, why="契约 switch.color_on=#38C172（成功色）"),
    dict(t="lamp", p={}, k="color", v="#38C172", wrong="#FF0000",
         why="契约 lamp.color=#38C172"),
    # 0 = 按控件宽高取小者，再扣掉两侧光晕（每边 8px）：光晕必须完整落在框内
    dict(t="lamp", p={}, k="size", v=min(RECT["w"], RECT["h"]) - 16, wrong=30,
         why="契约 lamp.size=0 = 短边扣掉光晕（200×80 → 80-16=64）"),
    dict(t="gauge", p={"min": 0, "max": 100, "style": "pointer", "color": "#2F70D0"},
         k="center_radius", v=6, wrong=22, bind={"value": {"tag": 0}},
         why="契约 gauge.center_radius=6"),

    # ── 表格：缺键必须能建页（旧行为 rows/cols 缺省 0 → 整页拒绝）──
    dict(t="table", p={}, k="header", v=True, wrong=None, exists_only=True,
         why="缺 rows/cols/header 也要按 3×3、无表头建页"),
]

HEAD = "%-12s %-14s %8s %8s  %s"


def main():
    apd = argparse.ArgumentParser()
    apd.add_argument("--filter", default=None)
    args = apd.parse_args()

    if not os.path.exists(ap.BIN):
        raise SystemExit("找不到 %s，先在 chenge-runtime 里 make HOST=1" % ap.BIN)
    os.makedirs(OUT_ROOT, exist_ok=True)

    print(HEAD % ("控件", "属性", "A≡B差", "A≉C差", "判读"))
    print("-" * 96)
    bad = []
    for c in CASES:
        wtype, key, v = c["t"], c["k"], c["v"]
        if args.filter and args.filter not in "%s.%s" % (wtype, key):
            continue
        rect = dict(RECT)
        rect.update(c.get("rect") or {})
        w = lambda tag, extra: ap.render(          # noqa: E731
            [ap.widget("w1", wtype, dict(c["p"], **extra), rect, bind=c.get("bind"))],
            "defaults/%s_%s_%s" % (wtype, key, tag), out_root=OUT_ROOT, env_extra=ENV)

        shot_a, log_a = w("absent", {})
        diff_b = 0
        if c.get("exists_only"):
            # 只要"建得起来"：整页不能因这个控件被拒（旧行为 rows/cols 缺省 0 → 整页失败）
            if "工程加载失败" in log_a:
                line = [l for l in log_a.splitlines() if "加载失败" in l]
                bad.append("%s.%s：缺键时整页被拒 —— %s" % (wtype, key, line[-1] if line else "?"))
        else:
            shot_b = w("contract", {key: v})[0]
            diff_b = shot_a.rect_diff(shot_b, rect["x"], rect["y"], rect["w"], rect["h"])
            if diff_b != 0:
                bad.append("%s.%s：缺键与契约缺省 %r 差 %d 像素（运行时缺省不是契约值）"
                           % (wtype, key, v, diff_b))

        diff_c = 0
        if c.get("wrong") is not None:
            shot_c = w("wrong", {key: c["wrong"]})[0]
            diff_c = shot_a.rect_diff(shot_c, rect["x"], rect["y"], rect["w"], rect["h"])
            if diff_c == 0:
                bad.append("%s.%s：把值改成 %r 位图也不动 —— 这条判据看不出差别（测不到）"
                           % (wtype, key, c["wrong"]))

        print(HEAD % (wtype, key, diff_b, diff_c if c.get("wrong") is not None else "-",
                      c.get("why", "")))

    print("-" * 96)
    if bad:
        for b in bad:
            print("  ✗ " + b)
        print("\n!! %d 条缺省不一致" % len(bad))
        return 1
    print("ok：%d 条缺省与契约逐像素一致" % len([c for c in CASES
                                            if not args.filter or args.filter in
                                            "%s.%s" % (c["t"], c["k"])]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
