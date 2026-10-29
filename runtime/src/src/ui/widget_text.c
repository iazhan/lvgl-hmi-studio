/*
 * widget_text.c — 纯显示文字：label / value / enum_text / ascii_display / message
 */
#include "widgets_internal.h"
#include "ui_font.h"
#include "core/i18n.h"
#include "data/text_conv.h"
#include "hmi_log.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* 文字超出框时的处理（契约 label.long_mode）。
 * 缺 long_mode 时回落到旧键 wrap（true = 换行 / false = 裁掉）——手写工程只写
 * wrap 也能得到正确结果，不必两个键都写。 */
static lv_label_long_mode_t label_long_mode(const hmi_json_t* props)
{
    const char* m = hmi_json_str(hmi_json_get(props, "long_mode"), NULL);

    if (!m) return hmi_json_bool(hmi_json_get(props, "wrap"), 1)
                       ? LV_LABEL_LONG_WRAP : LV_LABEL_LONG_CLIP;
    if (strcmp(m, "dots") == 0) return LV_LABEL_LONG_DOT;
    if (strcmp(m, "clip") == 0) return LV_LABEL_LONG_CLIP;
    if (strcmp(m, "scroll") == 0) return LV_LABEL_LONG_SCROLL;
    if (strcmp(m, "scroll_circular") == 0) return LV_LABEL_LONG_SCROLL_CIRCULAR;
    return LV_LABEL_LONG_WRAP;
}

int build_label(const hmi_json_t* w, const hmi_json_t* props,
                long x, long y, long ww, long hh, widget_t* out)
{
    const char* text = hmi_json_str(hmi_json_get(props, "text"), NULL);

    if (!text) {
        HMI_LOG("widget %s: label 缺 props.text", widget_id(w));
        return -1;
    }
    out->obj = lv_label_create(cur_parent());
    lv_obj_set_pos(out->obj, (int32_t)x, (int32_t)y);
    lv_obj_set_size(out->obj, (int32_t)ww, (int32_t)hh);
    lv_label_set_text(out->obj, i18n_text_static(text));
    lv_label_set_long_mode(out->obj, label_long_mode(props));
    lv_obj_set_style_border_width(out->obj, 0, 0);
    lv_obj_set_style_bg_opa(out->obj, LV_OPA_TRANSP, 0);
    lv_obj_set_style_pad_all(out->obj, 0, 0);
    /* 默认透明（标签多数要融进背景），配了底色/描边就按配置画 */
    apply_box_style(out->obj, props);
    apply_text_style(out->obj, props, 16);
    apply_text_valign(out->obj, props, hh, 16);
    out->type = W_LABEL;
    return 0;
}

/* ── value ── */
int build_value(const hmi_json_t* props,
                long x, long y, long ww, long hh, widget_t* out)
{
    const char* unit = hmi_json_str(hmi_json_get(props, "unit"), "");

    out->obj = lv_label_create(cur_parent());
    lv_obj_set_pos(out->obj, (int32_t)x, (int32_t)y);
    lv_obj_set_size(out->obj, (int32_t)ww, (int32_t)hh);
    lv_label_set_text(out->obj, "");
    lv_obj_set_style_border_width(out->obj, 0, 0);
    lv_obj_set_style_bg_opa(out->obj, LV_OPA_TRANSP, 0);
    lv_obj_set_style_pad_all(out->obj, 0, 0);
    /* 读数比正文大一号（契约 value.font_size 缺省 20，label 是 16） */
    apply_text_style(out->obj, props, 20);
    apply_text_valign(out->obj, props, hh, 20);

    snprintf(out->unit, sizeof(out->unit), "%s", unit);
    /* 数值前后缀（契约 value.prefix / suffix）：如 ¥ / %。只作用于 value，
     * 不作用于 input —— 输入框里带前缀会被用户当成内容一起编辑。 */
    snprintf(out->prefix, sizeof(out->prefix), "%s",
             hmi_json_str(hmi_json_get(props, "prefix"), ""));
    snprintf(out->suffix, sizeof(out->suffix), "%s",
             hmi_json_str(hmi_json_get(props, "suffix"), ""));
    out->decimals = (int)hmi_json_int(hmi_json_get(props, "decimals"), 0);
    if (out->decimals < 0) out->decimals = 0;
    if (out->decimals > 6) out->decimals = 6;
    out->type = W_VALUE;
    return 0;
}

/* ── button ── */
/* 枚举文本：文字完全可能由绑定给（工程里不写 props.text），所以不能复用
 * build_label 的「text 必填」判据 */
int build_enum_text(const hmi_json_t* w, const hmi_json_t* props,
                    long x, long y, long ww, long hh, widget_t* out)
{
    const char* text = hmi_json_str(hmi_json_get(props, "text"), "");
    lv_obj_t* o;

    LV_UNUSED(w);
    o = lv_label_create(cur_parent());
    if (!o) return -1;
    lv_obj_set_pos(o, (int32_t)x, (int32_t)y);
    lv_obj_set_size(o, (int32_t)ww, (int32_t)hh);
    lv_label_set_text(o, i18n_text_static(text));
    apply_text_style(o, props, 16);
    apply_text_valign(o, props, hh, 16);
    out->obj = o;
    out->type = W_ENUM_TEXT;
    return 0;
}

/* 输入框：v1 只做显示（文本由绑定刷新），编辑与键盘后置 */
void ui_ascii_display_set(widget_t* w, const char* raw, int n, int quality)
{
    char buf[64 * 3 + 1];

    if (!w || !w->obj) return;
    if (quality != TAG_Q_GOOD) {
        lv_label_set_text(w->obj, "---");     /* 质量不好时不留上一次的文字 */
        return;
    }
    (void)text_to_utf8(raw, n, w->u.ascii.text_gbk, buf, (int)sizeof(buf));
    text_limit_chars(buf, w->u.ascii.text_max);
    lv_label_set_text(w->obj, buf);
}

/* ── ascii_display：多字节文本（ADR-0007） ──
 * Tag DB 里是设备原始字节，这里按 encoding 转 UTF-8 再交给 LVGL（label 会自己拷贝）。 */
int build_ascii_display(const hmi_json_t* props, long x, long y, long ww, long hh,
                        widget_t* out)
{
    int max_len = (int)hmi_json_int(hmi_json_get(props, "max_len"), 32);
    const char* enc = hmi_json_str(hmi_json_get(props, "encoding"), "gbk");
    const char* align = hmi_json_str(hmi_json_get(props, "align"), "left");
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 16);
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#E6EDF3");
    lv_obj_t* o;

    if (max_len < 1) max_len = 1;
    if (max_len > 64) max_len = 64;
    if (fs < 10) fs = 10;
    if (fs > 32) fs = 32;
    if (!ui_color_valid(color)) return -1;
    if (strcmp(enc, "gbk") != 0 && strcmp(enc, "utf8") != 0) return -1;

    o = lv_label_create(cur_parent());
    if (!o) return -1;
    place(o, x, y, ww, hh);
    lv_obj_set_style_text_font(o, ui_font(fs), 0);
    lv_obj_set_style_text_color(o, ui_color_hex(color), 0);
    lv_obj_set_style_text_align(o, strcmp(align, "center") == 0 ? LV_TEXT_ALIGN_CENTER
                                  : strcmp(align, "right") == 0 ? LV_TEXT_ALIGN_RIGHT
                                  : LV_TEXT_ALIGN_LEFT, 0);
    lv_obj_set_style_bg_opa(o, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(o, 0, 0);
    lv_label_set_text(o, "");
    apply_text_valign(o, props, hh, 16);
    out->obj = o;
    out->type = W_ASCII_DISPLAY;
    out->u.ascii.text_max = max_len;
    out->u.ascii.text_gbk = (strcmp(enc, "gbk") == 0);
    return 0;
}

/* ── message：消息栏 ──
 * level 决定底色与描边；auto_hide_ms > 0 时到点把文字清空（绑定的值再变会重新计时）；
 * marquee 用 LVGL 的循环滚动，超宽不截断而是慢慢滚。 */
static void msg_hide_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);

    if (w) w->u.msg.msg_timer = NULL;            /* repeat_count=1，定时器由 LVGL 自删 */
    if (!w || !w->aux || !lv_obj_is_valid(w->aux)) return;
    lv_label_set_text(w->aux, "");
    if (getenv("HMI_RT_TRACE")) HMI_LOG("message: %dms 到点，已清空", w->u.msg.msg_hide_ms);
}

static void msg_arm(widget_t* w)
{
    if (!w || w->u.msg.msg_hide_ms <= 0) return;
    if (w->u.msg.msg_timer) {
        lv_timer_delete(w->u.msg.msg_timer);
        w->u.msg.msg_timer = NULL;
    }
    w->u.msg.msg_timer = lv_timer_create(msg_hide_cb, (uint32_t)w->u.msg.msg_hide_ms, w);
    if (w->u.msg.msg_timer) lv_timer_set_repeat_count(w->u.msg.msg_timer, 1);
}

static void msg_delete_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);

    if (w && w->u.msg.msg_timer) {               /* 拆页时把还没到点的定时器收掉 */
        lv_timer_delete(w->u.msg.msg_timer);
        w->u.msg.msg_timer = NULL;
    }
}

void ui_message_set_text(widget_t* w, const char* text)
{
    if (!w || !w->aux) return;
    lv_label_set_text(w->aux, text ? text : "");
    msg_arm(w);
}

int build_message(const hmi_json_t* props, long x, long y, long ww, long hh,
                  widget_t* out, char* err, size_t errsz)
{
    const char* text = hmi_json_str(hmi_json_get(props, "text"), NULL);
    const char* level = hmi_json_str(hmi_json_get(props, "level"), "info");
    const char* align = hmi_json_str(hmi_json_get(props, "align"), "left");
    long hide = hmi_json_int(hmi_json_get(props, "auto_hide_ms"), 0);
    int marquee = hmi_json_bool(hmi_json_get(props, "marquee"), 0);
    const char* bg = "#1F3550";
    const char* bd = "#2F70D0";
    lv_obj_t* o;
    lv_obj_t* lab;
    int lh, pad;

    if (!text) {
        snprintf(err, errsz, "message 缺 props.text");
        return -1;
    }
    if (strcmp(level, "warn") == 0) { bg = "#4A3E14"; bd = "#B8860B"; }
    else if (strcmp(level, "error") == 0) { bg = "#4A1F1F"; bd = "#C0392B"; }
    else if (strcmp(level, "info") != 0) {
        snprintf(err, errsz, "message 的 level=%s 非法（info/warn/error）", level);
        return -1;
    }
    if (strcmp(align, "left") != 0 && strcmp(align, "center") != 0 &&
        strcmp(align, "right") != 0) {
        snprintf(err, errsz, "message 的 align=%s 非法（left/center/right）", align);
        return -1;
    }
    if (hide < 0 || hide > 600000) {
        snprintf(err, errsz, "message 的 auto_hide_ms %ld 越界（0..600000）", hide);
        return -1;
    }

    o = lv_obj_create(cur_parent());
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    place(o, x, y, ww, hh);
    lv_obj_set_style_bg_color(o, ui_color_hex(bg), 0);
    lv_obj_set_style_border_color(o, ui_color_hex(bd), 0);
    lv_obj_set_style_border_width(o, 1, 0);
    lv_obj_set_style_radius(o, 6, 0);
    lv_obj_set_style_pad_all(o, 0, 0);

    lab = lv_label_create(o);
    lv_label_set_text(lab, i18n_text_static(text));
    lv_label_set_long_mode(lab, marquee ? LV_LABEL_LONG_SCROLL_CIRCULAR : LV_LABEL_LONG_CLIP);
    lh = (int)lv_font_get_line_height(ui_font(16));
    pad = ((int)hh - lh) / 2;
    if (pad < 0) pad = 0;
    lv_obj_set_pos(lab, 10, 0);
    lv_obj_set_size(lab, (int32_t)(ww > 20 ? ww - 20 : ww), (int32_t)hh);
    lv_obj_set_style_pad_top(lab, pad, 0);
    lv_obj_set_style_text_align(lab, strcmp(align, "center") == 0 ? LV_TEXT_ALIGN_CENTER
                                 : strcmp(align, "right") == 0 ? LV_TEXT_ALIGN_RIGHT
                                                               : LV_TEXT_ALIGN_LEFT, 0);
    lv_obj_set_style_text_font(lab, ui_font(16), 0);
    lv_obj_set_style_text_color(lab, ui_color_hex("#E6EDF3"), 0);
    lv_obj_set_style_bg_opa(lab, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(lab, 0, 0);

    out->obj = o;
    out->aux = lab;
    out->u.msg.msg_hide_ms = (int)hide;
    out->type = W_MESSAGE;
    lv_obj_add_event_cb(o, msg_delete_cb, LV_EVENT_DELETE, out);
    msg_arm(out);
    return 0;
}
