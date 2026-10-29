/*
 * widget_indicator.c — 显示量的：lamp / bar / gauge / pipe
 */
#include "widgets_internal.h"
#include "ui_font.h"
#include "ui_figure.h"     /* fig_fill_poly：管道按四边形自绘（见 pipe_path_stroke） */
#include "hmi_log.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* lv_led 的光晕每边超出灯珠本体多少像素：主题 shadow_width 15dpx + spread 5dpx，
 * 130dpi 下量到 8px。灯珠直径按它给光晕留位置，光晕才不会被框裁掉。 */
#define LAMP_GLOW 8

int build_lamp(const hmi_json_t* w, const hmi_json_t* props,
               long x, long y, widget_t* out)
{
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#38C172");
    long size = hmi_json_int(hmi_json_get(props, "size"), 0);
    long bright = hmi_json_int(hmi_json_get(props, "brightness"), 255);
    long bx = x, by = y, bw = 0, bh = 0;
    lv_obj_t* root;

    if (!ui_color_valid(color)) {
        HMI_LOG("widget %s: lamp props.color %s 不是 #RRGGBB", widget_id(w), color);
        return -1;
    }
    if (widget_rect(w, &bx, &by, &bw, &bh) != 0) {
        bx = x;
        by = y;
        bw = size > 0 ? size : 36;
        bh = bw;
    }
    /* 契约 lamp.size：0（或缺键）= 按控件宽高取小者。
     * 灯珠带光晕（每边 LAMP_GLOW），光晕必须**完整可见**：控件框是灯的外框，光晕溢出会被
     * 父容器裁成一圈硬边。所以灯珠直径 = 短边 - 2×LAMP_GLOW，光晕正好用到框边 ——
     * 光晕的观感（那圈柔和过渡）与"不裁时"一致，只是灯珠本体相应小一圈。
     * 显式给了正数才按那个直径画（那时光晕可能要框留边，由用户自己给）。 */
    if (size <= 0) {
        long box = (bw < bh ? bw : bh);

        size = box > 2 * LAMP_GLOW ? box - 2 * LAMP_GLOW : box;
    }
    if (size > 512) size = 512;
    if (size < 1) size = 1;
    /* 亮度（契约 lamp.brightness）：0 = 全暗，255 = 灯色原样。 */
    if (bright < 0) bright = 0;
    if (bright > 255) bright = 255;

    /* 灯珠装在**透明容器**里，容器才是控件的几何对象：
     * 设计器拖拽时走的是"只改 lv_obj 尺寸"的几何快路径（DesignHostPreview 的 W 补丁），
     * 若灯珠自己当控件对象，把框拖成长方形的那一瞬间它就被拉成椭圆，停手整页重建才
     * 变回圆 —— 画面上先椭圆后圆。容器接尺寸、灯珠按直径走，形状与框的比例无关。 */
    root = lv_obj_create(cur_parent());
    if (!root) return -1;
    place(root, bx, by, bw > 0 ? bw : size, bh > 0 ? bh : size);
    lv_obj_set_style_bg_opa(root, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(root, 0, 0);
    lv_obj_set_style_pad_all(root, 0, 0);
    lv_obj_remove_flag(root, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_remove_flag(root, LV_OBJ_FLAG_CLICKABLE);


    out->obj = root;
    out->aux = lv_led_create(root);        /* 灯珠：颜色/亮度都改它（见 widget_paint_obj） */
    lv_obj_set_size(out->aux, (int32_t)size, (int32_t)size);
    lv_obj_center(out->aux);               /* 容器改尺寸后重新居中，不贴左上角 */
    lv_led_set_color(out->aux, ui_color_hex(color));
    lv_led_set_brightness(out->aux, (uint8_t)bright);
    out->type = W_LAMP;
    return 0;
}

/* ── knob ──
 * 与 gauge 同一套角度（135°→45°，扫过 270°），区别是它可拖：LVGL 的 arc 自带
 * 拖动改值，手柄就是 LV_PART_KNOB。拖动期间连续写回（与 slider 一致），松手时
 * 按 step 吸附一次——手感停在刻度上，变量也拿到整齐的值。 */
static int s_knob_snap;          /* 防止"吸附写值"再触发一轮回调 */

static void knob_show(widget_t* w)
{
    char buf[24];
    int v = (int)lv_arc_get_value(w->obj);

    if (!w->aux) return;
    if (w->decimals > 0) snprintf(buf, sizeof(buf), "%.*f", w->decimals, (double)v);
    else snprintf(buf, sizeof(buf), "%d", v);
    lv_label_set_text(w->aux, buf);
}

static void knob_change_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);

    if (!w || s_knob_snap) return;
    knob_show(w);
}

/* 松手（或手指划出控件）时按 step 吸附一次。不能在拖动过程中吸附：
 * LVGL 的 arc 是"相对增量"式拖动，中途改值会把 last_angle 一起改掉，
 * 手感和增量都会乱。 */
static void knob_snap_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    int v, snapped;

    if (!w || s_knob_snap || w->step < 1) return;
    v = (int)lv_arc_get_value(w->obj);
    snapped = w->min + (int)(((double)(v - w->min) / w->step + 0.5) * w->step);
    if (snapped < w->min) snapped = w->min;
    if (snapped > w->max) snapped = w->max;
    if (snapped == v) return;
    s_knob_snap = 1;
    lv_arc_set_value(w->obj, snapped);
    s_knob_snap = 0;
    knob_show(w);
}

void ui_knob_set(widget_t* w, double v)
{
    int iv;

    if (!w || !w->obj) return;
    if (v < (double)w->min) v = (double)w->min;
    if (v > (double)w->max) v = (double)w->max;
    iv = (int)(v + 0.5);
    if ((int)lv_arc_get_value(w->obj) != iv) {
        s_knob_snap = 1;
        lv_arc_set_value(w->obj, iv);
        s_knob_snap = 0;
    }
    knob_show(w);
}

int build_knob(const hmi_json_t* props, long x, long y, long ww, long hh,
               widget_t* out)
{
    lv_obj_t* o = lv_arc_create(cur_parent());
    int mn = (int)hmi_json_int(hmi_json_get(props, "min"), 0);
    int mx = (int)hmi_json_int(hmi_json_get(props, "max"), 100);
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#2F70D0");
    int aw = (int)hmi_json_int(hmi_json_get(props, "arc_width"), 10);
    int dec = (int)hmi_json_int(hmi_json_get(props, "decimals"), 0);
    int show = hmi_json_bool(hmi_json_get(props, "show_value"), 1);
    double step = hmi_json_num(hmi_json_get(props, "step"), 1);

    if (!o || mx <= mn) return -1;
    if (aw < 2) aw = 2;
    if (aw > 30) aw = 30;
    if (dec < 0) dec = 0;
    if (dec > 3) dec = 3;
    if (step < 0) step = 0;

    place(o, x, y, ww, hh);
    lv_arc_set_bg_angles(o, 135, 45);
    lv_arc_set_range(o, mn, mx);
    lv_arc_set_value(o, mn);
    lv_obj_set_style_arc_color(o, ui_color_hex(color), LV_PART_INDICATOR);
    lv_obj_set_style_arc_color(o, ui_color_hex("#2A3440"), LV_PART_MAIN);
    lv_obj_set_style_arc_width(o, (int32_t)aw, LV_PART_MAIN);
    lv_obj_set_style_arc_width(o, (int32_t)aw, LV_PART_INDICATOR);
    /* 手柄：实心圆，比弧宽一圈，手指好按 */
    lv_obj_set_style_bg_color(o, ui_color_hex("#E6EDF3"), LV_PART_KNOB);
    lv_obj_set_style_bg_opa(o, LV_OPA_COVER, LV_PART_KNOB);

    out->obj = o;
    out->type = W_KNOB;
    out->min = mn;
    out->max = mx;
    out->step = step;
    out->decimals = dec;
    if (show) {
        lv_obj_t* l = lv_label_create(o);

        if (l) {
            lv_obj_set_style_text_font(l, ui_font(16), 0);
            lv_obj_set_style_text_color(l, ui_color_hex("#E6EDF3"), 0);
            lv_obj_align(l, LV_ALIGN_CENTER, 0, 0);
            out->aux = l;
            knob_show(out);
        }
    }
    lv_obj_add_event_cb(o, knob_change_cb, LV_EVENT_VALUE_CHANGED, out);
    lv_obj_add_event_cb(o, knob_snap_cb, LV_EVENT_RELEASED, out);
    lv_obj_add_event_cb(o, knob_snap_cb, LV_EVENT_PRESS_LOST, out);
    return 0;
}

/* ── spinner ──
 * 纯装饰：转起来就表示"在忙"，不接绑定、不产生事件。速度与弧长可配。 */
int build_spinner(const hmi_json_t* props, long x, long y, long ww, long hh,
                  widget_t* out)
{
    lv_obj_t* o = lv_spinner_create(cur_parent());
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#2F70D0");
    int aw = (int)hmi_json_int(hmi_json_get(props, "arc_width"), 4);
    int alen = (int)hmi_json_int(hmi_json_get(props, "arc_len"), 200);
    int sp = (int)hmi_json_int(hmi_json_get(props, "speed_ms"), 1000);

    if (!o || !ui_color_valid(color)) return -1;
    if (aw < 1 || aw > 24) aw = 4;
    if (alen < 10 || alen > 359) alen = 200;
    if (sp < 100 || sp > 10000) sp = 1000;
    place(o, x, y, ww, hh);
    lv_spinner_set_anim_params(o, (uint32_t)sp, (uint32_t)alen);
    /* MAIN 是整圈底纹、INDICATOR 才是转动的弧段：两者同色就成了一枚静态圆环，
     * 看不出在转。底纹压到 20% 不透明度，弧段给满。 */
    lv_obj_set_style_arc_width(o, (int32_t)aw, LV_PART_MAIN);
    lv_obj_set_style_arc_color(o, ui_color_hex(color), LV_PART_MAIN);
    lv_obj_set_style_arc_opa(o, LV_OPA_20, LV_PART_MAIN);
    lv_obj_set_style_arc_width(o, (int32_t)aw, LV_PART_INDICATOR);
    lv_obj_set_style_arc_color(o, ui_color_hex(color), LV_PART_INDICATOR);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_CLICKABLE);
    out->obj = o;
    out->type = W_SPINNER;
    return 0;
}

/* ── 总入口 ── */


/* ── 其余基础控件（37 §6.4 的 v1 子集） ── */
int build_bar(const hmi_json_t* props, long x, long y, long ww, long hh,
              widget_t* out)
{
    lv_obj_t* o = lv_bar_create(cur_parent());
    int mn = (int)hmi_json_int(hmi_json_get(props, "min"), 0);
    int mx = (int)hmi_json_int(hmi_json_get(props, "max"), 100);
    /* 契约键是 direction（horizontal/vertical）；orientation（h/v）是 v1 早期的别名，
     * 手写工程里可能还在用，保留。导出器只写 direction。 */
    const char* dir = hmi_json_str(hmi_json_get(props, "direction"),
                                   hmi_json_str(hmi_json_get(props, "orientation"), "horizontal"));
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#2F70D0");

    if (!o || mx <= mn) return -1;
    place(o, x, y, ww, hh);
    lv_bar_set_range(o, mn, mx);
    if (strcmp(dir, "vertical") == 0 || strcmp(dir, "v") == 0)
        lv_bar_set_orientation(o, LV_BAR_ORIENTATION_VERTICAL);
    lv_obj_set_style_bg_color(o, ui_color_hex(color), LV_PART_INDICATOR);
    if (hmi_json_bool(hmi_json_get(props, "show_percent"), 0)) {
        lv_obj_t* pct = lv_label_create(o);

        lv_obj_set_style_text_font(pct, ui_font(14), 0);
        lv_obj_set_style_text_color(pct, ui_color_hex("#E6EDF3"), 0);
        lv_obj_set_style_bg_opa(pct, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(pct, 0, 0);
        lv_label_set_text(pct, "0%");
        lv_obj_center(pct);
        out->aux = pct;
    }
    out->obj = o;
    out->type = W_BAR;
    out->min = mn;
    out->max = mx;
    return 0;
}

/* 指针式：270 度扫过（135 → 405，屏幕坐标 y 向下），缺口在正下方 */
void ui_gauge_pointer_set(widget_t* w, double v)
{
    double t = 0, a;

    if (w->max > w->min) t = (v - w->min) / (double)(w->max - w->min);
    if (t < 0) t = 0;
    if (t > 1) t = 1;
    a = (135.0 + 270.0 * t) * 3.14159265358979 / 180.0;
    w->u.gauge.pts[0].x = (lv_value_precise_t)w->u.gauge.cx;
    w->u.gauge.pts[0].y = (lv_value_precise_t)w->u.gauge.cy;
    /* 指针长度按 props.needle_len（百分比）缩放；0 视为默认 80（旧行为） */
    {
        int pct = w->u.gauge.needle_len > 0 ? w->u.gauge.needle_len : 80;
        double len = w->u.gauge.radius * (double)pct / 100.0;

        w->u.gauge.pts[1].x = (lv_value_precise_t)(w->u.gauge.cx + (int)(len * cos(a)));
        w->u.gauge.pts[1].y = (lv_value_precise_t)(w->u.gauge.cy + (int)(len * sin(a)));
    }
    lv_line_set_points(w->aux ? w->aux : w->obj, w->u.gauge.pts, 2);
    if (w->u.gauge.aux3) lv_arc_set_value(w->u.gauge.aux3, (int32_t)v);
    if (w->u.gauge.aux2) {
        char buf[24];

        snprintf(buf, sizeof(buf), "%.*f", w->decimals, v);
        lv_label_set_text(w->u.gauge.aux2, buf);
    }
}

void ui_gauge_set(widget_t* w, double v)
{
    char buf[24];

    if (w->u.gauge.style == 1) {
        ui_gauge_pointer_set(w, v);
        return;
    }
    if (!w->obj) return;
    lv_arc_set_value(w->obj, (int32_t)v);
    if (w->u.gauge.aux2) {
        snprintf(buf, sizeof(buf), "%.*f", w->decimals, v);
        lv_label_set_text(w->u.gauge.aux2, buf);
    }
}

/* 沿 135°→405° 等分画刻度线。ticks 是含两端的主刻度数量（2..11），
 * 端点数组由 lv_line 直接引用，所以存在 widget_t 里、拆页时释放。 */
static void gauge_draw_ticks(lv_obj_t* parent, widget_t* w, int ticks,
                             int cx, int cy, int r_out, int len, const char* color)
{
    if (ticks < 2 || ticks > 11) return;
    w->u.gauge.tick_pts = calloc((size_t)ticks * 2, sizeof(lv_point_precise_t));
    if (!w->u.gauge.tick_pts) return;
    w->u.gauge.tick_count = ticks;

    for (int i = 0; i < ticks; i++) {
        double a = (135.0 + 270.0 * (double)i / (double)(ticks - 1)) *
                   3.14159265358979 / 180.0;
        double ca = cos(a), sa = sin(a);
        lv_obj_t* ln = lv_line_create(parent);

        w->u.gauge.tick_pts[i * 2].x = (lv_value_precise_t)(cx + (int)((r_out - len) * ca));
        w->u.gauge.tick_pts[i * 2].y = (lv_value_precise_t)(cy + (int)((r_out - len) * sa));
        w->u.gauge.tick_pts[i * 2 + 1].x = (lv_value_precise_t)(cx + (int)(r_out * ca));
        w->u.gauge.tick_pts[i * 2 + 1].y = (lv_value_precise_t)(cy + (int)(r_out * sa));
        if (!ln) continue;
        lv_obj_set_pos(ln, 0, 0);
        lv_line_set_points(ln, &w->u.gauge.tick_pts[i * 2], 2);
        lv_obj_set_style_line_width(ln, 2, 0);
        lv_obj_set_style_line_color(ln, ui_color_hex(color), 0);
    }
}

int build_gauge(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out)
{
    lv_obj_t* o = lv_arc_create(cur_parent());
    int mn = (int)hmi_json_int(hmi_json_get(props, "min"), 0);
    int mx = (int)hmi_json_int(hmi_json_get(props, "max"), 100);
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#2F70D0");
    const char* style = hmi_json_str(hmi_json_get(props, "style"), "arc");

    /* 细节属性（契约 props）。**只有显式给了才改变外观**：缺省走历史值
     * （弧 8 / 指针环 6 / 刻度 6、7 / 指针 4px / 轴心半径 5），
     * 这样老工程与 golden 的渲染结果一个像素都不动。 */
    /* 只有一个弧宽：指针式的环与 arc 的弧必须同宽，否则勾上"显示指针"弧会变粗/变细 */
    /* 刻度只有一套几何：半径与刻度长在两种样式下都必须相同，否则勾上指针刻度会跳圈 */
    int aw_arc = 8, tl_arc = 6, nw = 4, cr = 6, nl = 80;
    int label_outside = 0, label_none = 0, show_value = 1;
    {
        const hmi_json_t* jaw = hmi_json_get(props, "arc_width");
        const hmi_json_t* jtl = hmi_json_get(props, "tick_len");
        const hmi_json_t* jnw = hmi_json_get(props, "needle_width");
        const hmi_json_t* jcr = hmi_json_get(props, "center_radius");

        if (jaw) {
            aw_arc = (int)hmi_json_int(jaw, 8);
            if (aw_arc < 1) aw_arc = 1;
            if (aw_arc > 64) aw_arc = 64;
        }
        if (jtl) {
            tl_arc = (int)hmi_json_int(jtl, 6);
            if (tl_arc < 1) tl_arc = 1;
            if (tl_arc > 32) tl_arc = 32;

        }
        if (jnw) {
            nw = (int)hmi_json_int(jnw, 4);
            if (nw < 1) nw = 1;
            if (nw > 32) nw = 32;
        }
        if (jcr) {
            cr = (int)hmi_json_int(jcr, 6);
            if (cr < 0) cr = 0;
            if (cr > 64) cr = 64;
        }
        nl = (int)(hmi_json_num(hmi_json_get(props, "needle_len"), 0.8) * 100.0);
        if (nl < 20) nl = 80;          /* 越界的小值按 0.8 处理（顶到弧） */
        if (nl > 100) nl = 100;
    }
    /* 两端数值的位置（契约 label_pos）：inside = 贴在弧内侧底部（旧行为），
     * outside = 放到控件外侧下方，none = 不画两端数值。取值非法按 inside。 */
    {
        const char* lp = hmi_json_str(hmi_json_get(props, "label_pos"), "inside");

        label_outside = strcmp(lp, "outside") == 0;
        label_none = strcmp(lp, "none") == 0;
    }
    show_value = hmi_json_bool(hmi_json_get(props, "show_value"), 1);

    if (!o || mx <= mn) return -1;
    place(o, x, y, ww, hh);
    /* 270 度圆弧，缺口在正下方；去掉旋钮，只做指示 */
    lv_arc_set_bg_angles(o, 135, 45);
    lv_arc_set_range(o, mn, mx);
    lv_arc_set_value(o, mn);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_arc_color(o, ui_color_hex(color), LV_PART_INDICATOR);
    /* 底色要够亮，否则小值时整圈几乎看不见，像一个空环 */
    lv_obj_set_style_arc_color(o, ui_color_hex("#2A3440"), LV_PART_MAIN);
    lv_obj_set_style_arc_width(o, (int32_t)aw_arc, LV_PART_MAIN);
    lv_obj_set_style_arc_width(o, (int32_t)aw_arc, LV_PART_INDICATOR);
    lv_obj_set_style_bg_opa(o, LV_OPA_TRANSP, LV_PART_KNOB);
    out->obj = o;
    out->type = W_GAUGE;
    out->min = mn;
    out->max = mx;
    out->decimals = (int)hmi_json_int(hmi_json_get(props, "decimals"), 0);
    {
        /* 两端刻度 + 弧心数值：面板给的那两行（label_pos / show_value）分别管它们，
         * 关掉就不建对象 —— 建了再藏会把读数留在命中测试与布局里。 */
        char buf[16];

        if (!label_none) {
            lv_obj_t* lmin = lv_label_create(o);
            lv_obj_t* lmax = lv_label_create(o);

            snprintf(buf, sizeof(buf), "%d", mn);
            lv_label_set_text(lmin, buf);
            lv_obj_set_style_text_font(lmin, ui_font(11), 0);
            lv_obj_set_style_text_color(lmin, ui_color_hex("#9AA7B5"), 0);
            lv_obj_align(lmin, label_outside ? LV_ALIGN_OUT_BOTTOM_LEFT : LV_ALIGN_BOTTOM_LEFT, 2, label_outside ? 4 : -2);

            snprintf(buf, sizeof(buf), "%d", mx);
            lv_label_set_text(lmax, buf);
            lv_obj_set_style_text_font(lmax, ui_font(11), 0);
            lv_obj_set_style_text_color(lmax, ui_color_hex("#9AA7B5"), 0);
            lv_obj_align(lmax, label_outside ? LV_ALIGN_OUT_BOTTOM_RIGHT : LV_ALIGN_BOTTOM_RIGHT, -2, label_outside ? 4 : -2);
        }
        if (show_value) {
            lv_obj_t* lval = lv_label_create(o);

            lv_label_set_text(lval, "0");
            lv_obj_set_style_text_font(lval, ui_font(16), 0);
            lv_obj_set_style_text_color(lval, ui_color_hex("#E6EDF3"), 0);
            lv_obj_align(lval, LV_ALIGN_CENTER, 0, 0);
            out->u.gauge.aux2 = lval;
        }

        out->u.gauge.cx = (int)ww / 2;
        out->u.gauge.cy = (int)hh / 2;
        out->u.gauge.radius = (int)((ww < hh ? ww : hh) / 2 - 10);
        gauge_draw_ticks(o, out,
                         (int)hmi_json_int(hmi_json_get(props, "ticks"), 0),
                         out->u.gauge.cx, out->u.gauge.cy, out->u.gauge.radius, tl_arc, "#4A5A6A");
    }
    if (strcmp(style, "pointer") == 0) {
        /* 指针用一根 lv_line 画：LVGL 没有旋转 API，直接按角度算端点更简单。
         *
         * 对象树：box（控件根，负责体积/底色/边框，与 arc 样式同一个角色）
         *          └ dial（透明画布，铺满 box）
         *              ├ ring / 刻度 / 两端数值 / 中心轴
         *              └ needle（w->aux，ui_gauge_pointer_set 改它的端点）
         * 根必须是 box 而不是 needle：把底色/边框刷在一根 line 上，勾上指针就会
         * 在指针周围冒出一块底色，看起来像"仪表背景变了"。 */
        lv_obj_t* box = lv_obj_create(cur_parent());
        lv_obj_t* dial;
        lv_obj_t* needle;

        if (!box) return -1;
        place(box, x, y, ww, hh);
        lv_obj_set_style_bg_opa(box, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(box, 0, 0);
        lv_obj_set_style_pad_all(box, 0, 0);
        lv_obj_remove_flag(box, LV_OBJ_FLAG_SCROLLABLE);

        dial = lv_obj_create(box);
        if (!dial) return -1;
        lv_obj_set_pos(dial, 0, 0);
        lv_obj_set_size(dial, (int32_t)ww, (int32_t)hh);
        lv_obj_set_style_bg_opa(dial, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(dial, 0, 0);
        /* 必须清零内边距：主题默认的 pad 会把子对象（指针/中心轴/刻度）整体偏移，
         * 指针枢轴就落不到弧心上 */
        lv_obj_set_style_pad_all(dial, 0, 0);
        lv_obj_remove_flag(dial, LV_OBJ_FLAG_SCROLLABLE);
        out->u.gauge.cx = (int)ww / 2;
        out->u.gauge.cy = (int)hh / 2;
        out->u.gauge.radius = (int)((ww < hh ? ww : hh) / 2 - 10);   /* 与 arc 样式同一半径 */
        {
            lv_obj_t* ring = lv_arc_create(dial);

            lv_obj_set_size(ring, (int32_t)ww, (int32_t)hh);
            lv_obj_center(ring);
            lv_arc_set_bg_angles(ring, 135, 45);
            lv_arc_set_range(ring, mn, mx);
            lv_arc_set_value(ring, mn);
            lv_obj_remove_flag(ring, LV_OBJ_FLAG_CLICKABLE);
            /* 指针只是**多画一根**，弧本身与 arc 样式一模一样（同宽同色、同样按值填充）：
             * 勾上"显示指针"就变细/变色的弧会被当成"样式变了"，而不是多了一个指针 */
            lv_obj_set_style_arc_width(ring, (int32_t)aw_arc, LV_PART_MAIN);
            lv_obj_set_style_arc_color(ring, ui_color_hex("#2A3440"), LV_PART_MAIN);
            lv_obj_set_style_arc_width(ring, (int32_t)aw_arc, LV_PART_INDICATOR);
            lv_obj_set_style_arc_color(ring, ui_color_hex(color), LV_PART_INDICATOR);
            lv_obj_set_style_bg_opa(ring, LV_OPA_TRANSP, LV_PART_KNOB);
            out->u.gauge.aux3 = ring;
        }
        /* 两端刻度：贴着弧的起点与终点（label_pos=none 时不做） */
        if (!label_none) {
            char buf[16];
            lv_obj_t* lmin = lv_label_create(dial);
            lv_obj_t* lmax = lv_label_create(dial);

            snprintf(buf, sizeof(buf), "%d", mn);
            lv_label_set_text(lmin, buf);
            lv_obj_set_style_text_font(lmin, ui_font(11), 0);
            lv_obj_set_style_text_color(lmin, ui_color_hex("#9AA7B5"), 0);
            lv_obj_align(lmin, label_outside ? LV_ALIGN_OUT_BOTTOM_LEFT : LV_ALIGN_BOTTOM_LEFT, 2, label_outside ? 4 : 0);

            snprintf(buf, sizeof(buf), "%d", mx);
            lv_label_set_text(lmax, buf);
            lv_obj_set_style_text_font(lmax, ui_font(11), 0);
            lv_obj_set_style_text_color(lmax, ui_color_hex("#9AA7B5"), 0);
            lv_obj_align(lmax, label_outside ? LV_ALIGN_OUT_BOTTOM_RIGHT : LV_ALIGN_BOTTOM_RIGHT, -2, label_outside ? 4 : 0);
        }
        /* 当前值：弧心下方（show_value=false 时不建） */
        if (show_value) {
            lv_obj_t* lv = lv_label_create(dial);

            lv_label_set_text(lv, "0");
            lv_obj_set_style_text_font(lv, ui_font(16), 0);
            lv_obj_set_style_text_color(lv, ui_color_hex("#E6EDF3"), 0);
            lv_obj_align(lv, LV_ALIGN_CENTER, 0, 40);
            out->u.gauge.aux2 = lv;
        }
        /* 中心轴 */
        {
            lv_obj_t* hub = lv_obj_create(dial);

            lv_obj_set_size(hub, (int32_t)(cr * 2), (int32_t)(cr * 2));
            lv_obj_set_pos(hub, (int32_t)out->u.gauge.cx - cr, (int32_t)out->u.gauge.cy - cr);
            lv_obj_set_style_radius(hub, LV_RADIUS_CIRCLE, 0);
            lv_obj_set_style_bg_color(hub, ui_color_hex("#E6EDF3"), 0);
            lv_obj_set_style_border_width(hub, 0, 0);
            lv_obj_remove_flag(hub, LV_OBJ_FLAG_SCROLLABLE);
        }
        gauge_draw_ticks(dial, out,
                         (int)hmi_json_int(hmi_json_get(props, "ticks"), 0),
                         out->u.gauge.cx, out->u.gauge.cy, out->u.gauge.radius, tl_arc, "#4A5A6A");
        needle = lv_line_create(dial);
        lv_obj_set_pos(needle, 0, 0);
        lv_obj_set_style_line_width(needle, (int32_t)nw, 0);
        lv_obj_set_style_line_color(needle, ui_color_hex(color), 0);
        lv_obj_set_style_line_rounded(needle, true, 0);
        lv_obj_delete(o);           /* 指针样式不用 arc 本体 */
        out->obj = box;             /* 根 = 控件盒（底色/边框/体积都归它） */
        out->aux = needle;
        out->u.gauge.style = 1;
        out->u.gauge.needle_len = nl;
        ui_gauge_pointer_set(out, (double)mn);
    }
    return 0;
}

#define PIPE_TICK_MS 40

/* ── pipe：管道 + 流动块 ──
 * 管壁取 border_color，通道取流动色的浅色版，流动块取 color；value 绑定为 0 时停住。
 * 动画是「偏移量 + 定时重绘」，不做逐帧图片，板上也不占额外内存。
 *
 * 两种走向：
 *   直管 —— 按 direction 在控件框内画一条水平/垂直管道；
 *   路径 —— props.points 给了 ≥2 点就沿这条折线画（直管 2 点、弯管 3 点、多段 N 点）。
 *            流动相位按**总弧长**累计，拐点处不重置，所以弯管/多段管的块是连续穿过去的。 */

/* 把弧长区间 [s0,s1] 沿折线画成一条带宽度的带子：管壁、通道、流动块共用这一个原语。
 *
 * 为什么不用 lv_draw_line 描粗线：
 *   · 圆头（round_start/end）会给两端各画一个 radius=LV_RADIUS_CIRCLE 的圆，粗管
 *     （十几到几十像素）撑爆 sw 掩码缓存直接段错误（实测 SIGSEGV 在
 *     lv_draw_sw_mask.c:circ_calc_aa4）；
 *   · 粗的**斜线**这版 LVGL 画出来边缘会歪（wcorr 加宽 + 逐像素掩码），管壁与流动块
 *     的切面看着不垂直。
 *
 * 所以自己算几何，而且是**整条带子一个多边形**：左侧偏移点顺序 + 右侧偏移点逆序，
 * 拐点用斜接点 (n0+n1)/(1+n0·n1)。
 *   · 「分段四边形 + 拐点补丁」的写法在每处拼接都会留一条 1px 缝 —— 两侧 AA 各覆盖
 *     一半，叠出来是一条暗线；亮色流动块上就是"拐弯处不平滑"（实测就是这个观感）；
 *   · 一条带子一次填充，接缝彻底没有；斜接保证任意夹角都不缺角、不戳出轮廓。
 */
#define PIPE_BAND_SPAN 40   /* 一次填充最多用多少个折线顶点（多边形点数 = 2×它 ≤ 82） */

/* 取 [s0,s1] 对应的子折线顶点（含两端截断点）。返回点数，0 = 空。 */
static int pipe_band_points(widget_t* w, float s0, float s1,
                            float* xs, float* ys, int max)
{
    float acc = 0.0f;
    int i, m = 0;

    for (i = 0; i + 1 < w->u.pipe.path_n && m + 1 < max; i++) {
        float len = w->u.pipe.seg_len[i];
        float p0 = acc, p1 = acc + len;
        float ux, uy, a, b;

        acc = p1;
        if (len <= 0.0f || p1 <= s0) continue;
        if (p0 >= s1) break;
        a = s0 > p0 ? s0 : p0;
        b = s1 < p1 ? s1 : p1;
        if (b <= a) continue;

        ux = (float)(w->u.pipe.path[i + 1].x - w->u.pipe.path[i].x) / len;
        uy = (float)(w->u.pipe.path[i + 1].y - w->u.pipe.path[i].y) / len;

        if (m == 0) {
            xs[m] = (float)w->u.pipe.path[i].x + ux * (a - p0);
            ys[m] = (float)w->u.pipe.path[i].y + uy * (a - p0);
            m++;
        }
        xs[m] = (float)w->u.pipe.path[i].x + ux * (b - p0);
        ys[m] = (float)w->u.pipe.path[i].y + uy * (b - p0);
        m++;
    }
    return (m >= 2) ? m : 0;
}

/* 子折线顶点 → 带子多边形（左偏移顺序 + 右偏移逆序）。返回多边形点数。 */
static int pipe_band_poly(const float* xs, const float* ys, int m, float half,
                          lv_point_precise_t* poly)
{
    int j;

    for (j = 0; j < m; j++) {
        float ux0, uy0, ux1, uy1, n0x, n0y, n1x, n1y, dot, mx, my, l;

        if (j == 0) { ux0 = xs[1] - xs[0]; uy0 = ys[1] - ys[0]; }
        else        { ux0 = xs[j] - xs[j - 1]; uy0 = ys[j] - ys[j - 1]; }
        if (j == m - 1) { ux1 = xs[j] - xs[j - 1]; uy1 = ys[j] - ys[j - 1]; }
        else            { ux1 = xs[j + 1] - xs[j]; uy1 = ys[j + 1] - ys[j]; }

        l = sqrtf(ux0 * ux0 + uy0 * uy0);
        if (l < 0.0001f) { ux0 = ux1; uy0 = uy1; l = 1.0f; }
        n0x = -uy0 / l * half;
        n0y = ux0 / l * half;

        l = sqrtf(ux1 * ux1 + uy1 * uy1);
        if (l < 0.0001f) { ux1 = ux0; uy1 = uy0; l = 1.0f; }
        n1x = -uy1 / l * half;
        n1y = ux1 / l * half;

        dot = (n0x * n1x + n0y * n1y) / (half * half);
        if (dot > 0.9999f || 1.0f + dot < 0.25f) {
            /* 近直线 / 近 180° 回折：不做斜接（回折处斜接点会飞到很远） */
            mx = n0x;
            my = n0y;
        } else {
            mx = (n0x + n1x) / (1.0f + dot);
            my = (n0y + n1y) / (1.0f + dot);
        }

        poly[j].x = (lv_value_precise_t)(xs[j] + mx);
        poly[j].y = (lv_value_precise_t)(ys[j] + my);
        poly[2 * m - 1 - j].x = (lv_value_precise_t)(xs[j] - mx);
        poly[2 * m - 1 - j].y = (lv_value_precise_t)(ys[j] - my);
    }
    return 2 * m;
}

static void pipe_path_stroke(lv_layer_t* layer, const lv_area_t* area, widget_t* w,
                             float s0, float s1, lv_color_t color, int width)
{
    lv_point_precise_t poly[2 * (PIPE_BAND_SPAN + 1)];
    float xs[PIPE_BAND_SPAN + 1], ys[PIPE_BAND_SPAN + 1];
    const float half = (float)width / 2.0f;
    int i, m, max = PIPE_BAND_SPAN + 1;

    if (s1 <= s0 || width <= 0) return;

    /* 顶点多时分窗填：每窗 max 个点、相邻两窗重合一个顶点 ⇒ 带子仍然连着，
     * 只在每 40 个顶点处多一条斜接缝（多段管远达不到这个量级）。 */
    m = pipe_band_points(w, s0, s1, xs, ys, max);
    if (m < 2) return;

    for (i = 0; i < m - 1; i += max - 1) {
        int cnt = (m - i < max) ? (m - i) : max;
        int n = pipe_band_poly(xs + i, ys + i, cnt, half, poly);

        fig_fill_poly(layer, area->x1, area->y1, poly, n, color, LV_OPA_COVER);
        if (cnt < max) break;
    }
}

static void pipe_draw_path(lv_layer_t* layer, widget_t* w, const lv_area_t* area)
{
    int wall = w->u.pipe.flow_wall;
    int outer = w->u.pipe.flow_w;
    int chan = outer - 2 * wall;
    int blk, step, off, i;
    float total = w->u.pipe.path_len;

    if (chan < 1) chan = 1;
    if (outer < chan + 2) outer = chan + 2;


    /* 管壁（整条路径描粗）+ 通道（细一圈盖在中间） */
    pipe_path_stroke(layer, area, w, 0.0f, total, w->u.pipe.flow_wall_c, outer);
    pipe_path_stroke(layer, area, w, 0.0f, total, w->u.pipe.flow_chan, chan);
    if (!w->u.pipe.flow_on || total <= 1.0f) return;

    /* 流动块：块长 = 通道 1/2，间隔 = 通道 3/2，沿弧长平铺 */
    blk = chan / 2;
    if (blk < 2) blk = 2;
    step = chan + blk;
    if (step < blk + 2) step = blk + 2;
    off = w->u.pipe.flow_ofs % step;

    for (i = -1; (float)(i * step + off) < total + (float)blk; i++) {
        float s0 = (float)(i * step + off);
        float s1 = s0 + (float)blk;

        if (s1 <= 0.0f || s0 >= total) continue;
        if (s0 < 0.0f) s0 = 0.0f;
        if (s1 > total) s1 = total;
        /* 反向：把块沿路径镜像。定时器仍只做单调累加，相位不翻转——
         * 否则每次取模都要处理负数，回绕处会跳一块。 */
        if (w->u.pipe.flow_rev) {
            float t0 = total - s1;
            float t1 = total - s0;

            s0 = t0;
            s1 = t1;
        }
        pipe_path_stroke(layer, area, w, s0, s1, w->u.pipe.flow_color, chan);
    }
}

static void pipe_draw_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    lv_obj_t* obj = lv_event_get_target_obj(e);
    lv_layer_t* layer = lv_event_get_layer(e);
    lv_area_t area;
    int ww, hh, thick, i;
    lv_draw_rect_dsc_t rd;

    if (!w || !layer) return;
    lv_obj_get_coords(obj, &area);
    ww = (int)(area.x2 - area.x1 + 1);
    hh = (int)(area.y2 - area.y1 + 1);
    if (ww < 8 || hh < 8) return;
    thick = w->u.pipe.flow_wall;

    if (w->u.pipe.path_n >= 2) {
        pipe_draw_path(layer, w, &area);
        return;
    }

    lv_draw_rect_dsc_init(&rd);
    rd.border_width = 0;
    rd.radius = 0;
    rd.bg_opa = LV_OPA_COVER;

    /* 管壁 + 通道 */
    rd.bg_color = w->u.pipe.flow_wall_c;
    {
        lv_area_t ra = { area.x1, area.y1, area.x2, area.y2 };

        lv_draw_rect(layer, &rd, &ra);
    }
    rd.bg_color = w->u.pipe.flow_chan;                           /* 通道：浅色底 */
    {
        lv_area_t ra;

        if (w->u.pipe.flow_dir == 0) {
            ra.x1 = area.x1;
            ra.x2 = area.x2;
            ra.y1 = area.y1 + thick;
            ra.y2 = area.y2 - thick;
        } else {
            ra.x1 = area.x1 + thick;
            ra.x2 = area.x2 - thick;
            ra.y1 = area.y1;
            ra.y2 = area.y2;
        }
        lv_draw_rect(layer, &rd, &ra);
    }

    if (!w->u.pipe.flow_on) return;            /* 停：只留空管道 */

    /* 流动块：块长 = 通道 1/2，间隔 = 通道 3/2，按 flow_ofs 平移 */
    {
        int chan = (w->u.pipe.flow_dir == 0) ? (hh - 2 * thick) : (ww - 2 * thick);
        int len = (w->u.pipe.flow_dir == 0) ? ww : hh;
        int blk = chan / 2;
        int step = chan + blk;
        int off;
        lv_area_t ra;

        if (blk < 2) blk = 2;
        if (step < blk + 2) step = blk + 2;
        off = w->u.pipe.flow_ofs % step;
        for (i = -1; i * step + off < len + blk; i++) {
            int s0 = i * step + off;
            int s1 = s0 + blk;

            if (s1 <= 0 || s0 >= len) continue;
            if (s0 < 0) s0 = 0;
            if (s1 > len) s1 = len;
            /* 反向流动：把块沿轴镜像（len - s1 .. len - s0）。定时器仍只做单调累加，
             * 相位不翻转——否则每次取模都要处理负数，回绕处会跳一块。 */
            if (w->u.pipe.flow_rev) {
                int t0 = len - s1;
                int t1 = len - s0;

                s0 = t0;
                s1 = t1;
            }
            if (w->u.pipe.flow_dir == 0) {
                ra.x1 = (int32_t)(area.x1 + s0);
                ra.x2 = (int32_t)(area.x1 + s1 - 1);
                ra.y1 = (int32_t)(area.y1 + thick);
                ra.y2 = (int32_t)(area.y2 - thick);
            } else {
                ra.x1 = (int32_t)(area.x1 + thick);
                ra.x2 = (int32_t)(area.x2 - thick);
                ra.y1 = (int32_t)(area.y1 + s0);
                ra.y2 = (int32_t)(area.y1 + s1 - 1);
            }
            rd.bg_color = w->u.pipe.flow_color;                  /* 流动块：color 本身 */
            lv_draw_rect(layer, &rd, &ra);
        }
    }
}

static void pipe_tick_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);
    int len;

    if (!w || !w->obj) return;
    if (!w->u.pipe.flow_on) return;
    if (w->u.pipe.path_n >= 2) {
        len = (int)w->u.pipe.path_len;          /* 路径模式：全程 = 整条折线长度 */
    } else {
        len = (w->u.pipe.flow_dir == 0) ? (int)lv_obj_get_width(w->obj)
                                        : (int)lv_obj_get_height(w->obj);
    }
    if (len < 8) return;
    /* 一个块在 flow_ms 内走完全程 */
    w->u.pipe.flow_ofs += len * PIPE_TICK_MS / (w->u.pipe.flow_ms > 0 ? w->u.pipe.flow_ms : 120);
    if (w->u.pipe.flow_ofs < 0) w->u.pipe.flow_ofs = 0;
    lv_obj_invalidate(w->obj);
}

void ui_pipe_set_flow(widget_t* w, int on)
{
    if (!w) return;
    w->u.pipe.flow_on = on ? 1 : 0;
    if (w->obj) lv_obj_invalidate(w->obj);
}

/* 读 props.points 到路径（相对控件左上角）；返回点数，<2 表示没有路径。 */
static int pipe_read_path(const hmi_json_t* props, widget_t* out)
{
    const hmi_json_t* arr = hmi_json_get(props, "points");
    size_t n, i;

    if (hmi_json_type(arr) != HMI_JSON_ARR) return 0;
    n = hmi_json_count(arr);
    if (n < 2) return 0;
    if (n > WT_PIPE_PATH_MAX) n = WT_PIPE_PATH_MAX;

    for (i = 0; i < n; i++) {
        const hmi_json_t* pt = hmi_json_at(arr, i);

        if (hmi_json_type(pt) != HMI_JSON_ARR || hmi_json_count(pt) != 2) return 0;
        out->u.pipe.path[i].x = (lv_value_precise_t)hmi_json_num(hmi_json_at(pt, 0), 0);
        out->u.pipe.path[i].y = (lv_value_precise_t)hmi_json_num(hmi_json_at(pt, 1), 0);
    }
    out->u.pipe.path_n = (int)n;
    return (int)n;
}

/* 把读到的点集映射进控件框，并定下管径 / 管壁厚度 / 每段长度。
 *
 * 管径来自 props.width（契约 pipe.width，缺省 12）——**不从框反推**：
 * 弯管/多段管的框短边往往就是半条腿长，按框推出来的管子会宽得像一块板（实测踩到过）。
 *
 * 映射规则（判据见 tools/pipe_path_probe.py）：
 *   · 管径钳到框内，内缩量 = 半个管径；
 *   · 某一轴跨度 ≤1（直线沿该轴走）⇒ 该轴取框中心；
 *   · 有跨度的轴等比映射进「框内缩半个管径」的区间 —— 编辑器按"包围盒四边各留半个管径"
 *     装框，于是这一步正好是**恒等**：点集坐标与看得见的管线中心线是同一条线。
 * 契约里 points 本就写着"按控件框等比缩放"，这段映射就是那句话的实现。 */
static void pipe_fit_path(widget_t* out, long ww, long hh, double wall, int dia)
{
    int n = out->u.pipe.path_n;
    float minx, miny, maxx, maxy, spanx, spany;
    int t, inset, room_x, room_y;
    int i;

    if (n < 2) return;

    minx = maxx = (float)out->u.pipe.path[0].x;
    miny = maxy = (float)out->u.pipe.path[0].y;
    for (i = 1; i < n; i++) {
        float x = (float)out->u.pipe.path[i].x;
        float y = (float)out->u.pipe.path[i].y;

        if (x < minx) minx = x;
        if (x > maxx) maxx = x;
        if (y < miny) miny = y;
        if (y > maxy) maxy = y;
    }
    spanx = maxx - minx;
    spany = maxy - miny;

    t = dia;
    if (t > ww) t = (int)ww;
    if (t > hh) t = (int)hh;
    if (t < 4) t = 4;
    inset = t / 2;
    room_x = (int)ww - 2 * inset;
    room_y = (int)hh - 2 * inset;

    for (i = 0; i < n; i++) {
        float x = (float)out->u.pipe.path[i].x;
        float y = (float)out->u.pipe.path[i].y;

        out->u.pipe.path[i].x = (lv_value_precise_t)((spanx > 1.0f && room_x > 0)
            ? (float)inset + (x - minx) * (float)room_x / spanx
            : (float)ww / 2.0f);
        out->u.pipe.path[i].y = (lv_value_precise_t)((spany > 1.0f && room_y > 0)
            ? (float)inset + (y - miny) * (float)room_y / spany
            : (float)hh / 2.0f);
    }

    out->u.pipe.flow_w = t;
    out->u.pipe.flow_wall = (int)(wall * (double)t + 0.5);
    if (out->u.pipe.flow_wall < 1) out->u.pipe.flow_wall = 1;
    if (out->u.pipe.flow_wall > t / 2 - 1) out->u.pipe.flow_wall = t / 2 - 1;
    if (out->u.pipe.flow_wall < 1) out->u.pipe.flow_wall = 1;

    out->u.pipe.path_len = 0.0f;
    for (i = 0; i + 1 < n; i++) {
        float dx = (float)(out->u.pipe.path[i + 1].x - out->u.pipe.path[i].x);
        float dy = (float)(out->u.pipe.path[i + 1].y - out->u.pipe.path[i].y);
        float len = sqrtf(dx * dx + dy * dy);

        out->u.pipe.seg_len[i] = len;
        out->u.pipe.path_len += len;
    }

}

int build_pipe(const hmi_json_t* props, long x, long y, long ww, long hh,
               widget_t* out)
{
    const char* dir = hmi_json_str(hmi_json_get(props, "direction"), "h");
    double wall = hmi_json_num(hmi_json_get(props, "wall"), 0.2);
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#4EA1FF");
    const char* chan = hmi_json_str(hmi_json_get(props, "channel_color"), "");
    const char* wall_c = hmi_json_str(hmi_json_get(props, "wall_color"), "");
    int flow_ms = (int)hmi_json_int(hmi_json_get(props, "flow_ms"), 120);
    int dia = (int)hmi_json_int(hmi_json_get(props, "width"), 12);
    lv_obj_t* o;
    int short_side;

    if (dia < 4) dia = 4;
    if (dia > 200) dia = 200;

    if (!ui_color_valid(color)) return -1;
    if (chan && *chan && !ui_color_valid(chan)) return -1;
    if (wall_c && *wall_c && !ui_color_valid(wall_c)) return -1;
    if (strcmp(dir, "h") != 0 && strcmp(dir, "v") != 0) return -1;
    if (flow_ms < 40) flow_ms = 40;
    if (flow_ms > 2000) flow_ms = 2000;
    short_side = (int)((strcmp(dir, "v") == 0) ? ww : hh);

    out->u.pipe.path_n = pipe_read_path(props, out);
    if (out->u.pipe.path_n >= 2)
        HMI_LOG("pipe: path %d points", out->u.pipe.path_n);

    o = lv_obj_create(cur_parent());
    if (!o) return -1;
    place(o, x, y, ww, hh);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_opa(o, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(o, 0, 0);
    out->obj = o;
    out->type = W_PIPE;
    out->u.pipe.flow_dir = (strcmp(dir, "v") == 0) ? 1 : 0;
    out->u.pipe.flow_rev =
        strcmp(hmi_json_str(hmi_json_get(props, "flow_dir"), "forward"), "reverse") == 0;
    out->u.pipe.flow_ms = flow_ms;
    if (out->u.pipe.path_n >= 2) {
        pipe_fit_path(out, ww, hh, wall, dia);  /* 管径自来 props.width，映射进框并算每段长度 */
    } else {
        out->u.pipe.flow_w = short_side;       /* 直管：框的短边就是管径（框 = 管子剖面） */
        out->u.pipe.flow_wall = (int)(wall * (double)short_side + 0.5);
        if (out->u.pipe.flow_wall < 1) out->u.pipe.flow_wall = 1;
        if (out->u.pipe.flow_wall > short_side / 2 - 1) out->u.pipe.flow_wall = short_side / 2 - 1;
    }
    out->u.pipe.flow_color = ui_color_hex(color);
    /* 通道底色：给了就用，没给按流动色提亮（保持老工程观感不变） */
    out->u.pipe.flow_chan = (chan && *chan) ? ui_color_hex(chan)
                                     : lv_color_lighten(out->u.pipe.flow_color, 120);
    /* 管壁：给了 wall_color 就用，否则沿用公共属性 border_color */
    out->u.pipe.flow_wall_c = (wall_c && *wall_c) ? ui_color_hex(wall_c)
                                           : ui_color_hex("#2A3A44");
    out->u.shape.shape_border = out->u.pipe.flow_wall_c;
    out->u.pipe.flow_on = 1;
    out->u.pipe.flow_ofs = 0;
    lv_obj_add_event_cb(o, pipe_draw_cb, LV_EVENT_DRAW_MAIN, out);
    out->u.pipe.flow_timer = lv_timer_create(pipe_tick_cb, PIPE_TICK_MS, out);
    if (!out->u.pipe.flow_timer) return -1;
    return 0;
}
