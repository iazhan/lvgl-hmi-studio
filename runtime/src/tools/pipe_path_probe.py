#!/usr/bin/env python3
"""管道走向 / 直线端点的**像素级**判据（板端同源产物 `chenge-runtime` + HMI_SHOT）。

判的是三件在日志里看不见、只有量位图才知道的事：

  1. **路径模式真的沿 points 走** —— 沿折线中心线逐点采样必须全是"管道色"（非页底），
     而包围盒的**空角**必须是页底。换成旧的"按 direction 填一条直带"实现，
     弯管的竖腿与斜管的对角都会落回页底 ⇒ 本判据红。
  2. **拐点不缺口** —— 采样点包含拐点本身，那里必须有颜色（管壁/通道把拐点包住）。
  3. **直线带 points 是真线段** —— `kind=line` + `points` 必须是沿两点的细线，
     包围盒另一个角是页底；旧实现（整框填成窄矩形）在这里会红。

探针的工程按**编辑器的装框规则**生成（见下 `fit_pipe`）：只有按同一套规则装框，
"点集坐标"与"看得见的管线中心线"才是同一条线 —— 这正是判据要守住的不变式。
运行时那一侧的规则在 `src/ui/widget_indicator.c` 的 `pipe_fit_path()`。

流动相位连续性**不**由本脚本判：相位是时间驱动的、截图时刻不确定；脚本只判
"几条腿上都有流动块"，相位连续由 `pipe_path_stroke` 的单一弧长参数保证。

用法：
    python3 tools/pipe_path_probe.py           # 0 = 全过，1 = 有判据红
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from appearance_probe import BG, render, widget  # noqa: E402

# 页底与三种管道色都拉开距离，取色才判得准
WALL = "#5A6B7A"
CHAN = "#1E2A36"
FLOW = "#4EA1FF"
LINE_C = "#F2B84B"

PIPE_WIDTH = 12              # 与编辑器的 FigureGeometry.DefaultPipeThickness / 契约 pipe.width 同值


def hex_of(rgb):
    return "#%02X%02X%02X" % rgb


def fit_pipe(points, width=PIPE_WIDTH):
    """按编辑器的 FitPipe 规则装框，返回 (box, local_points)。

    规则只有一条：**包围盒四边各留半个管径**（管径是独立参数，不从框反推 ——
    弯管的框短边往往就是半条腿长，按框推出来的管子会宽得像一块板）。
    """
    pad = width / 2
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    x, y = min(xs) - pad, min(ys) - pad
    w = max(10.0, max(xs) - min(xs) + 2 * pad)
    h = max(10.0, max(ys) - min(ys) + 2 * pad)
    local = [[round(px - x), round(py - y)] for (px, py) in points]
    return (x, y, w, h), local


def pipe_widget(wid, points, **props):
    # 装框必须按**这条管道的实际管径**留边，否则框比管子窄、宿主只能把外沿裁掉
    box, local = fit_pipe(points, props.get("width", PIPE_WIDTH))
    p = {"points": local, "wall": 0.2, "color": FLOW, "width": PIPE_WIDTH,
         "channel_color": CHAN, "wall_color": WALL, "flow_ms": 600}
    p.update(props)
    return widget(wid, "pipe", p,
                  rect={"x": round(box[0]), "y": round(box[1]),
                        "w": round(box[2]), "h": round(box[3])})


def sample_path(bmp, points, n=24):
    """沿折线按弧长均匀采样，返回 [(x, y, is_bg)]（点是屏幕绝对坐标）。"""
    segs = []
    total = 0.0
    for i in range(len(points) - 1):
        x0, y0 = points[i]
        x1, y1 = points[i + 1]
        seg = ((x1 - x0) ** 2 + (y1 - y0) ** 2) ** 0.5
        segs.append((x0, y0, x1, y1, seg))
        total += seg
    out = []
    for k in range(n + 1):
        s = total * k / n
        acc = 0.0
        for (x0, y0, x1, y1, seg) in segs:
            if acc + seg >= s or seg == 0:
                t = 0.0 if seg == 0 else (s - acc) / seg
                x = x0 + (x1 - x0) * t
                y = y0 + (y1 - y0) * t
                out.append((x, y, hex_of(bmp.rgb(int(x), int(y))) == BG))
                break
            acc += seg
    return out


def count_hex(bmp, box, want, y_from=None, y_to=None):
    """框内某颜色的像素数（可只数纵向区间），用来判"每条腿上都有流动块"。"""
    x, y, w, h = box
    n = 0
    for yy in range(int(y), int(y + h)):
        if y_from is not None and not (y_from <= yy < y_to):
            continue
        for xx in range(int(x), int(x + w)):
            if hex_of(bmp.rgb(xx, yy)) == want:
                n += 1
    return n


def walk_path(points, s):
    """弧长 s 处的 (x, y, 单位法向)。法向 = (-uy, ux)，指向走向的右手侧。"""
    acc = 0.0
    for i in range(len(points) - 1):
        x0, y0 = points[i]
        x1, y1 = points[i + 1]
        seg = ((x1 - x0) ** 2 + (y1 - y0) ** 2) ** 0.5
        if seg == 0:
            continue
        if acc + seg >= s:
            t = (s - acc) / seg
            ux, uy = (x1 - x0) / seg, (y1 - y0) / seg
            return (x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, -uy, ux)
        acc += seg
    x0, y0 = points[-2]
    x1, y1 = points[-1]
    seg = ((x1 - x0) ** 2 + (y1 - y0) ** 2) ** 0.5 or 1.0
    return (x1, y1, -(y1 - y0) / seg, (x1 - x0) / seg)


def path_length(points):
    return sum(((points[i + 1][0] - points[i][0]) ** 2 +
                (points[i + 1][1] - points[i][1]) ** 2) ** 0.5
               for i in range(len(points) - 1))


def wall_samples(bmp, points, offsets):
    """沿整条路径取"离中心线 offset 像素"的采样点，返回 [(x, y, 是否页底)]。

    这是"管壁有没有脱落"的直接判据：斜管/拐角若用轴对齐方块补，offset 处的采样
    会掉进页底（那正是用户看到的"管壁脱坡"）。
    """
    total = path_length(points)
    out = []
    for k in range(len(offsets)):
        off = offsets[k]
        for j in range(41):
            x, y, nx, ny = walk_path(points, total * j / 40)
            px, py = int(round(x + nx * off)), int(round(y + ny * off))
            out.append((px, py, hex_of(bmp.rgb(px, py)) == BG))
    return out


def dist_to_path(points, px, py):
    """点到折线的最短距离（判断"有没有东西戳到管壁外面"用）。"""
    best = 1e9
    for i in range(len(points) - 1):
        x0, y0 = points[i]
        x1, y1 = points[i + 1]
        dx, dy = x1 - x0, y1 - y0
        seg2 = dx * dx + dy * dy
        t = 0.0 if seg2 == 0 else max(0.0, min(1.0, ((px - x0) * dx + (py - y0) * dy) / seg2))
        ex, ey = x0 + dx * t, y0 + dy * t
        d = ((px - ex) ** 2 + (py - ey) ** 2) ** 0.5
        if d < best:
            best = d
    return best


def main():
    fails = []

    def check(name, ok, detail=""):
        print(("ok   " if ok else "FAIL ") + name + ("" if ok else "  " + detail))
        if not ok:
            fails.append(name)

    # ── ① 弯管：三点（左下 → 右下 → 右上）──
    elbow = [(60, 260), (160, 260), (160, 80)]
    shot, log = render([pipe_widget("p_elbow", elbow)], "pipe-path-elbow")
    box, _ = fit_pipe(elbow)
    # 首末采样点就是两端端点：平头端点恰好画到端点前一个像素，端点本身允许取到页底
    holes = [f"({int(x)},{int(y)})" for (x, y, bg) in sample_path(shot, elbow)[1:-1] if bg]
    check("弯管：中心线全程有管道（拐点不缺口）", not holes, "缺色点 " + ", ".join(holes[:6]))
    check("弯管：日志确认走了路径模式", "pipe: path 3 points" in log, log[-400:])
    # 空角：L 形的**内侧缺口**（左上）必须是页底；退回"按 direction 填一条直带"时这里会被填满
    notch = hex_of(shot.rgb(int(box[0]) + 10, int(box[1]) + 10))
    check("弯管：框的空角（L 内侧）是页底", notch == BG, f"左上 {notch}，期望页底 {BG}")

    # 流动块：上下两半各至少有一块 —— 相位沿整条路径算，两条腿同相
    mid_y = 170
    upper = count_hex(shot, box, FLOW, None, mid_y)
    lower = count_hex(shot, box, FLOW, mid_y, box[1] + box[3])
    check("弯管：两条腿上都有流动块", upper > 0 and lower > 0, f"上 {upper}px / 下 {lower}px")

    # ── ② 多段管：四点 ──
    multi = [(240, 260), (330, 260), (330, 140), (420, 140)]
    shot2, log2 = render([pipe_widget("p_multi", multi)], "pipe-path-multi")
    holes2 = [f"({int(x)},{int(y)})"
              for (x, y, bg) in sample_path(shot2, multi, n=32)[1:-1] if bg]
    check("多段管：中心线全程有管道", not holes2, "缺色点 " + ", ".join(holes2[:6]))
    check("多段管：日志确认走了路径模式", "pipe: path 4 points" in log2, log2[-400:])
    mbox, _ = fit_pipe(multi)
    mnotch = hex_of(shot2.rgb(int(mbox[0]) + 12, int(mbox[1]) + 12))
    check("多段管：框的空角是页底", mnotch == BG, f"左上 {mnotch}，期望页底 {BG}")

    # ── ③ 斜直管：两点对角线 ──
    diag = [(460, 300), (600, 440)]
    shot3, _ = render([pipe_widget("p_diag", diag)], "pipe-path-diagonal")
    dbox, _ = fit_pipe(diag)
    diag_holes = [f"({int(x)},{int(y)})"
                  for (x, y, bg) in sample_path(shot3, diag, n=16)[1:-1] if bg]
    check("斜直管：沿对角线有管道", not diag_holes, "缺色点 " + ", ".join(diag_holes[:6]))
    # 空角：包围盒的空角必须还是页底（旧实现是整框一条横带 / 或整框填满 ⇒ 这里红）
    empty_ok = hex_of(shot3.rgb(int(dbox[0] + dbox[2]) - 6, int(dbox[1]) + 6)) == BG and \
               hex_of(shot3.rgb(int(dbox[0]) + 6, int(dbox[1] + dbox[3]) - 6)) == BG
    check("斜直管：框的空角仍是页底", empty_ok,
          f"右上 {shot3.hex(int(dbox[0] + dbox[2]) - 6, int(dbox[1]) + 6)} "
          f"左下 {shot3.hex(int(dbox[0]) + 6, int(dbox[1] + dbox[3]) - 6)}")

    # ── ④ 直管（两点水平）：框高就是管径 ──
    straight = [(60, 430), (300, 430)]
    shot5, log5 = render([pipe_widget("p_straight", straight)], "pipe-path-straight")
    sbox, _ = fit_pipe(straight)
    s_holes = [f"({int(x)},{int(y)})"
               for (x, y, bg) in sample_path(shot5, straight, n=12)[1:-1] if bg]
    check("直管：中心线有管道", not s_holes, "缺色点 " + ", ".join(s_holes[:6]))
    # 管径贴住框：框内紧贴上下边缘的位置必须是管道色（不是页底）
    top_ok = hex_of(shot5.rgb(int(sbox[0]) + 30, int(sbox[1]) + 1)) != BG
    bot_ok = hex_of(shot5.rgb(int(sbox[0]) + 30, int(sbox[1] + sbox[3]) - 2)) != BG
    check("直管：管径填满框高（上下边缘都是管道色）", top_ok and bot_ok,
          f"上 {shot5.hex(int(sbox[0]) + 30, int(sbox[1]) + 1)} "
          f"下 {shot5.hex(int(sbox[0]) + 30, int(sbox[1] + sbox[3]) - 2)}")
    check("直管：日志确认走了路径模式", "pipe: path 2 points" in log5, log5[-400:])

    # ── ⑤ 直线（kind=line + points）：真线段而不是填充窄矩形 ──
    line_box = (480, 380, 200, 80)
    line_pts = [[0, 80], [200, 0]]
    shot4, log4 = render(
        [widget("l_line", "shape",
                {"kind": "line", "points": line_pts, "border_color": LINE_C,
                 "border_width": 3, "fill_color": "#2A3A44"},
                rect={"x": line_box[0], "y": line_box[1], "w": line_box[2], "h": line_box[3]})],
        "line-points")
    mid_hex = shot4.hex(line_box[0] + 100, line_box[1] + 40)
    check("直线：中点是线段色", mid_hex == LINE_C, f"中点 {mid_hex}，期望 {LINE_C}")
    corner_hex = shot4.hex(line_box[0] + 6, line_box[1] + 6)
    check("直线：框角落不是填充（没退化成整框矩形）", corner_hex == BG,
          f"左上角 {corner_hex}，期望页底 {BG}")
    check("直线：日志确认带 points", "figure: line with 2 points" in log4, log4[-400:])

    # ── ⑥ 拐角几何：90° 不许缺口、45° 不许戳出轮廓 ──
    # 管径取 24（半宽 12）：老实现是"两段粗线 + 一块**轴对齐方块**"——
    #   90° 时斜接尖端只有 12×√2≈17，方块与粗线的接缝会在 12..17 之间留楔形洞；
    #   45° 时方块却会伸出 12×√2≈17px，比真正的斜接（≈13px）明显戳出管壁。
    elbow90 = [(160, 380), (460, 380), (460, 120)]
    shot6a, _ = render([pipe_widget("p_e90", elbow90, width=24)], "pipe-path-elbow90")
    half6 = 12.0
    # 拐角的**外**侧：两段单位方向之差指向"折线的外角"（内侧那块本来就该是页底）
    (x0, y0), (vx, vy), (x2, y2) = elbow90
    l1 = ((vx - x0) ** 2 + (vy - y0) ** 2) ** 0.5
    l2 = ((x2 - vx) ** 2 + (y2 - vy) ** 2) ** 0.5
    bx, by = (vx - x0) / l1 - (x2 - vx) / l2, (vy - y0) / l1 - (y2 - vy) / l2
    bl = (bx * bx + by * by) ** 0.5
    bx, by = bx / bl, by / bl
    holes = []
    # 采样到 13.5 为止：90° 的斜接尖端在 17px（那里只有亚像素厚，AA 会半透），
    # 12.5~13.5 是斜接的实心段 —— 没补斜接时这块正好是楔形洞
    for dist in (12.5, 13.0, 13.5):
        for side in (-1.0, 0.0, 1.0):
            px = int(round(vx + bx * dist - by * side))
            py = int(round(vy + by * dist + bx * side))
            if hex_of(shot6a.rgb(px, py)) == BG:
                holes.append(f"({px},{py})")
    check("90°弯管：拐角外侧斜接处无页底洞（管壁不脱坡）", not holes,
          f"{len(holes)} 个洞，例如 " + ", ".join(holes[:6]))

    elbow45 = [(120, 380), (420, 380), (620, 180)]
    shot6b, _ = render([pipe_widget("p_e45", elbow45, width=24)], "pipe-path-elbow45")
    total6 = path_length(elbow45)
    spill = []
    for j in range(0, 121):
        x, y, nx, ny = walk_path(elbow45, total6 * j / 120)
        for off in (half6 + 4.0, -(half6 + 4.0)):
            px, py = int(round(x + nx * off)), int(round(y + ny * off))
            if hex_of(shot6b.rgb(px, py)) != BG and dist_to_path(elbow45, px, py) > half6 + 4:
                spill.append(f"({px},{py})")
    check("45°弯管：管壁外 4px 处无颜色（拐角不会戳出轮廓）", not spill,
          "外溢点 " + ", ".join(spill[:6]))

    # ── ⑦ 背景色不能把控件矩形刷成底色 ──
    bg_probe = [(120, 300), (400, 300)]
    shot7a, _ = render([pipe_widget("p_bg", bg_probe)], "pipe-nobg")
    shot7b, _ = render([pipe_widget("p_bg", bg_probe, bg_color="#FF0000")], "pipe-withbg")
    b7, _ = fit_pipe(bg_probe)
    corner7 = shot7b.hex(int(b7[0]) + 2, int(b7[1]) + 1)
    check("管道：设了背景色也不刷整个矩形（框角仍是页底）", corner7 == BG,
          f"框角 {corner7}，期望页底 {BG}")

    # ── ⑧ 背景色不该改到流动色/通道色：两张图必须逐字节一致 ──
    same = shot7a.data == shot7b.data if hasattr(shot7a, "data") else None
    if same is None:
        diff = sum(1 for y in range(480) for x in range(800)
                   if shot7a.hex(x, y) != shot7b.hex(x, y))
        same = diff == 0
        detail = f"{diff} 像素不同"
    else:
        detail = "字节不同"
    check("管道：改背景色不影响管道自身的颜色（两张图一致）", same, detail)

    print()
    if fails:
        print("失败 %d 项：%s" % (len(fails), "、".join(fails)))
        return 1
    print("全部通过（9 组几何判据）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
