/*
 * widget_action.c — 按下就干活的：button / switch / slider
 */
#include "widgets_internal.h"
#include "ui_font.h"
#include "core/i18n.h"
#include "hmi_log.h"

#include <stdio.h>
#include <string.h>

static void btn_press_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);

    if (!w) return;
    w->u.btn.press_tick = lv_tick_get();
    if (w->u.btn.press_text[0] && w->aux) lv_label_set_text(w->aux, w->u.btn.press_text);
    if (w->u.btn.has_text_pressed && w->aux)
        lv_obj_set_style_text_color(w->aux, w->u.btn.text_pressed, 0);
}

static void btn_release_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);

    if (!w) return;
    if (w->u.btn.press_text[0] && w->aux) lv_label_set_text(w->aux, w->u.btn.last_text);
    if (w->u.btn.has_text_pressed && w->aux)
        lv_obj_set_style_text_color(w->aux, w->u.btn.text_normal, 0);
}

int build_button(const hmi_json_t* w, const hmi_json_t* props,
                 long x, long y, long ww, long hh, widget_t* out)
{
    const char* text = hmi_json_str(hmi_json_get(props, "text"), NULL);
    const char* bg = hmi_json_str(hmi_json_get(props, "bg_color"), NULL);
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 16);
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#FFFFFF");
    int radius = (int)hmi_json_int(hmi_json_get(props, "radius"), -1);
    const char* border = hmi_json_str(hmi_json_get(props, "border_color"), NULL);
    int border_w = (int)hmi_json_int(hmi_json_get(props, "border_width"), 0);
    const hmi_json_t* pressed = hmi_json_get(props, "pressed");
    const char* off_style = hmi_json_str(hmi_json_get(props, "off_style"), "dim");
    long hold = hmi_json_int(hmi_json_get(props, "hold_ms"), 0);
    long lpm = hmi_json_int(hmi_json_get(props, "long_press_ms"), 800);
    const hmi_json_t* cf = hmi_json_get(props, "confirm");
    lv_obj_t* lbl;

    if (!text) {
        HMI_LOG("widget %s: button 缺 props.text", widget_id(w));
        return -1;
    }
    if (bg && !ui_color_valid(bg)) {
        HMI_LOG("widget %s: button props.bg %s 不是 #RRGGBB", widget_id(w), bg);
        return -1;
    }
    if (border && !ui_color_valid(border)) {
        HMI_LOG("widget %s: button props.border_color %s 不是 #RRGGBB", widget_id(w), border);
        return -1;
    }
    if (strcmp(off_style, "dim") != 0 && strcmp(off_style, "hide") != 0) {
        HMI_LOG("widget %s: button props.off_style=%s 非法（dim/hide）", widget_id(w), off_style);
        return -1;
    }
    if (hold < 0 || hold > 5000) {
        HMI_LOG("widget %s: button props.hold_ms %ld 越界（0..5000）", widget_id(w), hold);
        return -1;
    }
    if (lpm < 300 || lpm > 5000) {
        HMI_LOG("widget %s: button props.long_press_ms %ld 越界（300..5000）",
                widget_id(w), lpm);
        return -1;
    }
    if (hmi_json_type(cf) == HMI_JSON_OBJ) {
        const char* ctext = hmi_json_str(hmi_json_get(cf, "text"), NULL);
        const char* cyes = hmi_json_str(hmi_json_get(cf, "yes"), NULL);
        const char* cno = hmi_json_str(hmi_json_get(cf, "no"), NULL);
        long wait = hmi_json_int(hmi_json_get(cf, "wait_ms"), 0);

        if (!ctext || !*ctext) {
            HMI_LOG("widget %s: button props.confirm 缺 text", widget_id(w));
            return -1;
        }
        if (wait < 0 || wait > 600000) {
            HMI_LOG("widget %s: button props.confirm.wait_ms %ld 越界（0..600000）",
                    widget_id(w), wait);
            return -1;
        }
        /* 存原文（可能带 @id），弹框时按当时语言渲染——中途切语言也能对上 */
        snprintf(out->u.btn.cf_text, sizeof(out->u.btn.cf_text), "%s", ctext);
        if (cyes) snprintf(out->u.btn.cf_yes, sizeof(out->u.btn.cf_yes), "%s", cyes);
        if (cno) snprintf(out->u.btn.cf_no, sizeof(out->u.btn.cf_no), "%s", cno);
        out->u.btn.cf_wait_ms = (int)wait;
    }

    out->obj = lv_button_create(cur_parent());
    lv_obj_set_pos(out->obj, (int32_t)x, (int32_t)y);
    lv_obj_set_size(out->obj, (int32_t)ww, (int32_t)hh);
    if (bg) lv_obj_set_style_bg_color(out->obj, ui_color_hex(bg), 0);
    if (radius >= 0) lv_obj_set_style_radius(out->obj, (int32_t)radius, 0);
    if (border) lv_obj_set_style_border_color(out->obj, ui_color_hex(border), 0);
    if (border_w > 0) lv_obj_set_style_border_width(out->obj, (int32_t)border_w, 0);

    /* 按下态：底色与边框用 LVGL 的状态样式（自动生效），文字与文字色要手工换 */
    if (hmi_json_type(pressed) == HMI_JSON_OBJ) {
        const char* pbg = hmi_json_str(hmi_json_get(pressed, "bg_color"), NULL);
        const char* pborder = hmi_json_str(hmi_json_get(pressed, "border_color"), NULL);
        const char* ptext = hmi_json_str(hmi_json_get(pressed, "text"), NULL);
        const char* pcolor = hmi_json_str(hmi_json_get(pressed, "text_color"), NULL);

        if (pbg) {
            if (!ui_color_valid(pbg)) {
                HMI_LOG("widget %s: pressed.bg 非法", widget_id(w));
                return -1;
            }
            lv_obj_set_style_bg_color(out->obj, ui_color_hex(pbg), LV_STATE_PRESSED);
        }
        if (pborder) {
            if (!ui_color_valid(pborder)) {
                HMI_LOG("widget %s: pressed.border_color 非法", widget_id(w));
                return -1;
            }
            lv_obj_set_style_border_color(out->obj, ui_color_hex(pborder), LV_STATE_PRESSED);
            if (border_w <= 0)
                lv_obj_set_style_border_width(out->obj, 1, LV_STATE_PRESSED);
        }
        if (ptext) snprintf(out->u.btn.press_text, sizeof(out->u.btn.press_text), "%s", ptext);
        if (pcolor) {
            if (!ui_color_valid(pcolor)) {
                HMI_LOG("widget %s: pressed.text_color 非法", widget_id(w));
                return -1;
            }
            out->u.btn.text_pressed = ui_color_hex(pcolor);
            out->u.btn.has_text_pressed = 1;
        }
    }

    lbl = lv_label_create(out->obj);
    {
        char tb[160];

        lv_label_set_text(lbl, i18n_text(text, tb, sizeof(tb)));
    }
    lv_obj_set_style_text_font(lbl, ui_font(fs), 0);
    lv_obj_set_style_text_color(lbl, ui_color_hex(color), 0);
    lv_obj_center(lbl);
    out->aux = lbl;          /* bind.text 要改的是这个标签，不是按钮本身 */
    out->u.btn.text_normal = ui_color_hex(color);
    snprintf(out->u.btn.last_text, sizeof(out->u.btn.last_text), "%s", text);
    out->hold_ms = (int)hold;
    out->long_press_ms = (int)lpm;
    out->off_hide = (strcmp(off_style, "hide") == 0);
    out->type = W_BUTTON;

    lv_obj_add_event_cb(out->obj, btn_press_cb, LV_EVENT_PRESSED, out);
    lv_obj_add_event_cb(out->obj, btn_release_cb, LV_EVENT_RELEASED, out);
    lv_obj_add_event_cb(out->obj, btn_release_cb, LV_EVENT_PRESS_LOST, out);
    return 0;
}

/* ── lamp ── */
int build_switch(const hmi_json_t* props, long x, long y, long ww, long hh,
                 widget_t* out)
{
    lv_obj_t* o = lv_switch_create(cur_parent());
    const char* on = hmi_json_str(hmi_json_get(props, "color_on"), "#38C172");
    /* color_off 是"断开时的轨道色"；没配它时用通用背景色兜底 —— 开关没有独立的
     * 背景层，轨道就是它的背景（面板上「背景色」对这一类就是这个语义）。 */
    const char* off = hmi_json_str(hmi_json_get(props, "color_off"),
                                   hmi_json_str(hmi_json_get(props, "bg_color"), NULL));
    const char* t_on = hmi_json_str(hmi_json_get(props, "text_on"), "");
    const char* t_off = hmi_json_str(hmi_json_get(props, "text_off"), "");

    if (!o) return -1;
    place(o, x, y, ww, hh);
    lv_obj_set_style_bg_color(o, ui_color_hex(on),
                              LV_PART_INDICATOR | LV_STATE_CHECKED);
    if (off) lv_obj_set_style_bg_color(o, ui_color_hex(off), LV_PART_MAIN);
    {
        /* 滑块颜色（契约 knob_color）：缺省跟随主题，给了才覆盖 */
        const char* knob = hmi_json_str(hmi_json_get(props, "knob_color"), NULL);

        if (knob) lv_obj_set_style_bg_color(o, ui_color_hex(knob), LV_PART_KNOB);
    }
    /* 通断文字可写 @id：渲染后存进控件，绑定按状态切换时直接用 */
    i18n_text(t_on, out->u.sw.text_on, sizeof(out->u.sw.text_on));
    i18n_text(t_off, out->u.sw.text_off, sizeof(out->u.sw.text_off));
    if (out->u.sw.text_on[0] || out->u.sw.text_off[0]) {
        lv_obj_t* l = lv_label_create(cur_parent());

        if (!l) return -1;
        lv_obj_set_pos(l, (int32_t)x + (int32_t)ww + 8, (int32_t)y + 8);
        lv_obj_set_style_text_font(l, ui_font(16), 0);
        lv_obj_set_style_text_color(l, ui_color_hex("#E6EDF3"), 0);
        lv_obj_set_style_border_width(l, 0, 0);
        lv_obj_set_style_bg_opa(l, LV_OPA_TRANSP, 0);
        lv_obj_set_style_pad_all(l, 0, 0);
        lv_label_set_text(l, out->u.sw.text_off[0] ? out->u.sw.text_off : out->u.sw.text_on);
        out->aux = l;
    }
    out->obj = o;
    out->type = W_SWITCH;
    return 0;
}

/* ── checkbox ──
 * 与 switch 的分工：勾选语义（同意/启用某项）用 checkbox，通断状态用 switch。
 * 勾选状态走 bind.checked（可写回），props.checked 只是初值。 */
int build_checkbox(const hmi_json_t* props, long x, long y, long ww, long hh,
                   widget_t* out)
{
    lv_obj_t* o = lv_checkbox_create(cur_parent());
    const char* text = hmi_json_str(hmi_json_get(props, "text"), "");
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#E6EDF3");
    const char* box = hmi_json_str(hmi_json_get(props, "box_color"), "#2F70D0");
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 16);
    char tb[160];

    if (!o) return -1;
    if (!ui_color_valid(color) || !ui_color_valid(box)) return -1;
    place(o, x, y, ww, hh);
    lv_checkbox_set_text(o, i18n_text(text, tb, sizeof(tb)));
    lv_obj_set_style_text_font(o, ui_font(fs), 0);
    lv_obj_set_style_text_color(o, ui_color_hex(color), 0);
    /* 方块：未勾是 box_color 描边，勾上用 box_color 填满 */
    lv_obj_set_style_border_color(o, ui_color_hex(box), LV_PART_INDICATOR);
    lv_obj_set_style_border_width(o, 2, LV_PART_INDICATOR);
    lv_obj_set_style_bg_color(o, ui_color_hex(box),
                              LV_PART_INDICATOR | LV_STATE_CHECKED);
    lv_obj_set_style_bg_opa(o, LV_OPA_COVER, LV_PART_INDICATOR | LV_STATE_CHECKED);
    if (hmi_json_bool(hmi_json_get(props, "checked"), 0))
        lv_obj_add_state(o, LV_STATE_CHECKED);
    out->obj = o;
    out->type = W_CHECKBOX;
    return 0;
}

/* ── keyboard ──
 * 只建一个空容器：键位在 ui_page 建完所有控件后由 keyboard_attach 挂进来
 * （那时才知道 target 是谁）。容器必须够大，否则内联键盘会被裁掉。 */
int build_keyboard(const hmi_json_t* props, long x, long y, long ww, long hh,
                   widget_t* out)
{
    lv_obj_t* o = lv_obj_create(cur_parent());

    (void)props;
    if (!o) return -1;
    place(o, x, y, ww, hh);
    lv_obj_set_style_bg_opa(o, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(o, 0, 0);
    lv_obj_set_style_pad_all(o, 0, 0);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    out->obj = o;
    out->type = W_KEYBOARD;
    return 0;
}

/* ── tabview ──
 * 页签内容由 ui_page 压平成普通控件，建对象时挂到各页签的内容页上
 * （见 s_wtab）；这里只建容器与页签本身。 */
int build_tabview(const hmi_json_t* props, long x, long y, long ww, long hh,
                  widget_t* out, char* err, size_t errsz)
{
    const hmi_json_t* tabs = hmi_json_get(props, "tabs");
    const char* pos = hmi_json_str(hmi_json_get(props, "tab_pos"), "top");
    int th = (int)hmi_json_int(hmi_json_get(props, "tab_h"), 40);
    lv_dir_t dir = LV_DIR_TOP;
    lv_obj_t* tv;
    size_t n;

    if (hmi_json_type(tabs) != HMI_JSON_ARR || hmi_json_count(tabs) < 1) {
        snprintf(err, errsz, "tabview 缺 props.tabs 数组");
        return -1;
    }
    n = hmi_json_count(tabs);
    if (n > WT_TAB_MAX) {
        snprintf(err, errsz, "tabview 页签 %zu 个，超过上限 %d", n, WT_TAB_MAX);
        return -1;
    }
    if (strcmp(pos, "bottom") == 0) dir = LV_DIR_BOTTOM;
    else if (strcmp(pos, "left") == 0) dir = LV_DIR_LEFT;
    else if (strcmp(pos, "right") == 0) dir = LV_DIR_RIGHT;
    if (th < 24) th = 24;
    if (th > 80) th = 80;

    tv = lv_tabview_create(cur_parent());
    if (!tv) return -1;
    place(tv, x, y, ww, hh);
    lv_tabview_set_tab_bar_position(tv, dir);
    lv_tabview_set_tab_bar_size(tv, (int32_t)th);
    for (size_t i = 0; i < n; i++) {
        const hmi_json_t* t = hmi_json_at(tabs, i);
        const char* title = hmi_json_str(hmi_json_get(t, "title"), "");
        char tb[64];

        out->u.tab.pages[i] = lv_tabview_add_tab(tv, i18n_text(title, tb, sizeof(tb)));
        if (!out->u.tab.pages[i]) return -1;
    }
    out->u.tab.npages = (int)n;
    out->obj = tv;
    out->type = W_TABVIEW;

    /* 外观按本应用的深色底来：LVGL 默认主题是浅色的，直接摆上去是一块白板；
     * 页签标题还会落 LV_FONT_DEFAULT（纯 ASCII）变成方框，必须显式换字体。 */
    {
        lv_obj_t* bar = lv_tabview_get_tab_bar(tv);
        lv_obj_t* content = lv_tabview_get_content(tv);

        lv_obj_set_style_bg_opa(tv, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(tv, 0, 0);
        lv_obj_set_style_radius(tv, 0, 0);
        lv_obj_set_style_pad_all(tv, 0, 0);
        if (content) {
            lv_obj_set_style_bg_opa(content, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(content, 0, 0);
            lv_obj_set_style_pad_all(content, 0, 0);
            /* 可滚动但不要那根滚动条：组态页面按整屏排版，条子只会压住右侧内容 */
            lv_obj_set_scrollbar_mode(content, LV_SCROLLBAR_MODE_OFF);
        }
        if (bar) {
            lv_obj_set_style_bg_opa(bar, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(bar, 0, 0);
            for (uint32_t k = 0; k < lv_obj_get_child_count(bar); k++) {
                lv_obj_t* btn = lv_obj_get_child(bar, (int32_t)k);

                if (!btn) continue;
                lv_obj_set_style_bg_opa(btn, LV_OPA_TRANSP, 0);
                lv_obj_set_style_bg_color(btn, ui_color_hex("#2F70D0"), LV_STATE_CHECKED);
                lv_obj_set_style_bg_opa(btn, LV_OPA_30, LV_STATE_CHECKED);
                lv_obj_set_style_shadow_width(btn, 0, 0);
                for (uint32_t j = 0; j < lv_obj_get_child_count(btn); j++) {
                    lv_obj_t* lbl = lv_obj_get_child(btn, (int32_t)j);

                    if (!lbl) continue;
                    lv_obj_set_style_text_font(lbl, ui_font(16), 0);
                    lv_obj_set_style_text_color(lbl, ui_color_hex("#C8D2DC"), 0);
                }
            }
        }
    }
    return 0;
}

int build_slider(const hmi_json_t* props, long x, long y, long ww, long hh,
                 widget_t* out)
{
    lv_obj_t* o = lv_slider_create(cur_parent());
    int mn = (int)hmi_json_int(hmi_json_get(props, "min"), 0);
    int mx = (int)hmi_json_int(hmi_json_get(props, "max"), 100);
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#2F70D0");

    if (!o || mx <= mn) return -1;
    place(o, x, y, ww, hh);
    lv_slider_set_range(o, mn, mx);
    lv_obj_set_style_bg_color(o, ui_color_hex(color), LV_PART_INDICATOR);
    out->step = hmi_json_num(hmi_json_get(props, "step"), 0);
    if (out->step < 0) out->step = 0;
    out->obj = o;
    out->type = W_SLIDER;
    out->min = mn;
    out->max = mx;
    return 0;
}
