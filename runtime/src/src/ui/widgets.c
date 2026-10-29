/*
 * widgets.c — 控件分发入口：类型解析、属性白名单校验、按类型建控件
 *
 * 每种控件的建法在 widget_*.c 里（见 widgets_internal.h 的清单）；本文件只负责
 * "认类型、验属性、调对应的 build_*"，以及 states 的应用顺序。
 */
#include "widgets_internal.h"
#include "widgets_schema.h"       /* 生成物：属性白名单，见 tools/gen_schema.py */
#include "widget_chart.h"
#include "widget_alarm.h"
#include "widget_recipe.h"
#include "ui_figure.h"
#include "ui_font.h"
#include "hmi_log.h"
#include "hmi_board.h"     /* HMI_LCD_WIDTH/HEIGHT：控件越界校验 */

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ── 旋转与镜像（ADR-0019 三档）──
 *   图片：旋转用 LVGL 原生 rotation/pivot（无缓冲代价）；镜像直接翻像素——
 *     9.5 的负缩放（set_scale_x(-256)）不生效，负值会被当巨大缩放，对象直接消失。
 *   图元：多边形族把旋转烘进顶点（无代价）；非多边形（rect/circle 等）走 style transform。
 *   文本类（含按钮这类带文字的对象）：只能 90° 倍数且 ≤256×256——靠 style transform，
 *     代价是一块 w×h×4 的临时图层，所以必须限尺寸；**不支持镜像**（同上，无负缩放）。
 *   容器类（tabview / keyboard / 带子控件的图元）：禁止，建页期报错。 */
#define ROT_TEXT_MAX 256

/* 图片镜像：在已加载的像素缓冲上就地翻转（RGB565 / RGB565A8 两种平面布局） */
static void image_flip_pixels(widget_t* w, int mx, int my)
{
    lv_image_dsc_t* d = &w->u.img.dsc;
    int ww = (int)d->header.w, hh = (int)d->header.h;
    uint8_t* base = (uint8_t*)w->u.img.img_buf;
    int planes = (d->header.cf == LV_COLOR_FORMAT_RGB565A8) ? 2 : 1;

    if (!base || ww <= 0 || hh <= 0) return;
    for (int p = 0; p < planes; p++) {
        int px_bpp = (p == 0) ? 2 : 1;           /* 平面 0 = RGB565，平面 1 = A8 */
        size_t stride = (size_t)ww * (size_t)px_bpp;
        uint8_t* plane = base + (size_t)p * (size_t)ww * (size_t)hh * 2;

        if (mx) {
            for (int y = 0; y < hh; y++) {
                uint8_t* row = plane + (size_t)y * stride;

                for (int x = 0; x < ww / 2; x++) {
                    uint8_t* a = row + (size_t)x * (size_t)px_bpp;
                    uint8_t* b = row + (size_t)(ww - 1 - x) * (size_t)px_bpp;

                    for (int k = 0; k < px_bpp; k++) {
                        uint8_t t = a[k];
                        a[k] = b[k];
                        b[k] = t;
                    }
                }
            }
        }
        if (my) {
            uint8_t* tmp = malloc(stride);
            if (!tmp) return;
            for (int y = 0; y < hh / 2; y++) {
                uint8_t* r0 = plane + (size_t)y * stride;
                uint8_t* r1 = plane + (size_t)(hh - 1 - y) * stride;

                memcpy(tmp, r0, stride);
                memcpy(r0, r1, stride);
                memcpy(r1, tmp, stride);
            }
            free(tmp);
        }
    }
}

static int is_text_class(wtype_t t)
{
    switch (t) {
    case W_LABEL: case W_VALUE: case W_ENUM_TEXT: case W_INPUT:
    case W_COMBOBOX: case W_BUTTON: case W_CHECKBOX: case W_ASCII_DISPLAY:
    case W_MESSAGE: case W_ALARM_BAR: case W_ROLLER:
        return 1;
    default:
        return 0;
    }
}

/* 图元：绕自身中心旋转（角度为正 = 顺时针），镜像先做 */
static void shape_transform(widget_t* w, int ang, int mx, int my)
{
    double a, ca, sa, cx, cy;
    int i;

    if (!w->u.shape.poly_pts || w->u.shape.poly_n <= 0) return;
    cx = (double)lv_obj_get_width(w->obj) / 2.0;
    cy = (double)lv_obj_get_height(w->obj) / 2.0;
    a = (double)ang * 3.14159265358979 / 180.0;
    ca = cos(a);
    sa = sin(a);
    for (i = 0; i < w->u.shape.poly_n; i++) {
        double x = (double)w->u.shape.poly_pts[i].x - cx;
        double y = (double)w->u.shape.poly_pts[i].y - cy;

        if (mx) x = -x;
        if (my) y = -y;
        w->u.shape.poly_pts[i].x = (lv_value_precise_t)lround(cx + x * ca - y * sa);
        w->u.shape.poly_pts[i].y = (lv_value_precise_t)lround(cy + x * sa + y * ca);
    }
}

static int apply_transform(widget_t* w, const hmi_json_t* props, int ww, int hh,
                           const char* wid, char* err, size_t errsz)
{
    int ang = (int)hmi_json_int(hmi_json_get(props, "rotation"), 0);
    int mx = hmi_json_bool(hmi_json_get(props, "mirror_x"), 0);
    int my = hmi_json_bool(hmi_json_get(props, "mirror_y"), 0);

    if (ang == 0 && !mx && !my) return 0;
    ang %= 360;
    if (ang < 0) ang += 360;
    if (!w->obj) return 0;
    /* 尺寸用调用方给的 ww/hh，不用 lv_obj_get_width/height：建页这一刻对象还没走过
     * 布局，coords 可能还是 0，拿它算 pivot 会得到 (0,0) —— 于是绕**左上角**转，
     * 位图和编辑器里的选中框就错开了（实测：绕中心转 vs 绕左上角转差半个尺寸）。 */

    if (w->type == W_IMAGE) {
        if (mx || my) {
            image_flip_pixels(w, mx, my);
            lv_image_set_src(w->obj, &w->u.img.dsc);   /* 让 LVGL 丢掉旧的解码缓存 */
        }
        lv_image_set_pivot(w->obj, ww / 2, hh / 2);
        if (ang) lv_image_set_rotation(w->obj, ang * 10);
        return 0;
    }
    if (w->type == W_SHAPE) {
        /* 带子控件的图元就是容器（container 之类），不允许旋转 */
        if (lv_obj_get_child_count(w->obj) > 0) {
            snprintf(err, errsz, "控件 %s: 容器类图元不支持旋转/镜像（ADR-0019）", wid);
            return -1;
        }
        if (w->u.shape.poly_pts && w->u.shape.poly_n > 0) {
            shape_transform(w, ang, mx, my);      /* 多边形族：烘进顶点，零代价 */
            return 0;
        }
        /* rect / round_rect / circle：样式变换只做旋转；镜像要翻顶点，这里没有 */
        if (mx || my) {
            snprintf(err, errsz,
                     "控件 %s: 该图元不支持镜像（只有多边形族的顶点可翻），请改用 polygon",
                     wid);
            return -1;
        }
        if (ang) lv_obj_set_style_transform_rotation(w->obj, ang * 10, 0);
        lv_obj_set_style_transform_pivot_x(w->obj, ww / 2, 0);
        lv_obj_set_style_transform_pivot_y(w->obj, hh / 2, 0);
        return 0;
    }
    if (w->type == W_TABVIEW || w->type == W_KEYBOARD) {
        snprintf(err, errsz, "控件 %s: 容器类控件不支持旋转/镜像（ADR-0019）", wid);
        return -1;
    }
    if (!is_text_class(w->type)) {
        snprintf(err, errsz, "控件 %s: 该类型不支持旋转/镜像（ADR-0019 只放开图片、图元与文本类）",
                 wid);
        return -1;
    }
    /* 文本类：只允许 90° 倍数，且离屏缓冲有上限 */
    if (mx || my) {
        snprintf(err, errsz,
                 "控件 %s: 文本类不支持镜像（LVGL 9.5 无负缩放），需要镜像请用图片或图元",
                 wid);
        return -1;
    }
    if (ang % 90 != 0) {
        snprintf(err, errsz,
                 "控件 %s: 文本类只能 0/90/180/270 度（当前 %d），任意角只对图片与图元开放",
                 wid, ang);
        return -1;
    }
    if (ww > ROT_TEXT_MAX || hh > ROT_TEXT_MAX) {
        snprintf(err, errsz,
                 "控件 %s: 旋转的文本类控件不得超过 %d×%d（当前 %d×%d），"
                 "否则离屏缓冲会吃掉内存",
                 wid, ROT_TEXT_MAX, ROT_TEXT_MAX, ww, hh);
        return -1;
    }
    if (ang) lv_obj_set_style_transform_rotation(w->obj, ang * 10, 0);
    lv_obj_set_style_transform_pivot_x(w->obj, ww / 2, 0);
    lv_obj_set_style_transform_pivot_y(w->obj, hh / 2, 0);
    return 0;
}

int widget_type_from_str(const char* s, wtype_t* out)
{
    if (!s) return -1;
    if (strcmp(s, "label") == 0)  { *out = W_LABEL;  return 0; }
    if (strcmp(s, "value") == 0)  { *out = W_VALUE;  return 0; }
    if (strcmp(s, "button") == 0) { *out = W_BUTTON; return 0; }
    if (strcmp(s, "lamp") == 0)   { *out = W_LAMP;   return 0; }
    if (strcmp(s, "switch") == 0) { *out = W_SWITCH; return 0; }
    if (strcmp(s, "bar") == 0)    { *out = W_BAR;    return 0; }
    if (strcmp(s, "gauge") == 0)  { *out = W_GAUGE;  return 0; }
    if (strcmp(s, "slider") == 0) { *out = W_SLIDER; return 0; }
    if (strcmp(s, "enum_text") == 0) { *out = W_ENUM_TEXT; return 0; }
    if (strcmp(s, "input") == 0)  { *out = W_INPUT;  return 0; }
    if (strcmp(s, "combobox") == 0) { *out = W_COMBOBOX; return 0; }
    if (strcmp(s, "shape") == 0)  { *out = W_SHAPE;  return 0; }
    if (strcmp(s, "state_image") == 0) { *out = W_STATE_IMAGE; return 0; }
    if (strcmp(s, "table") == 0)  { *out = W_TABLE;  return 0; }
    if (strcmp(s, "qrcode") == 0) { *out = W_QRCODE; return 0; }
    if (strcmp(s, "realtime_curve") == 0) { *out = W_REALTIME_CURVE; return 0; }
    if (strcmp(s, "history_curve") == 0)  { *out = W_HISTORY_CURVE;  return 0; }
    if (strcmp(s, "xy_curve") == 0)       { *out = W_XY_CURVE;       return 0; }
    if (strcmp(s, "chart") == 0)          { *out = W_CHART;          return 0; }
    if (strcmp(s, "roller") == 0)         { *out = W_ROLLER;         return 0; }
    if (strcmp(s, "ascii_display") == 0)  { *out = W_ASCII_DISPLAY;  return 0; }
    if (strcmp(s, "pipe") == 0)           { *out = W_PIPE;           return 0; }
    if (strcmp(s, "frame_anim") == 0)     { *out = W_FRAME_ANIM;     return 0; }
    if (strcmp(s, "history_table") == 0)  { *out = W_HISTORY_TABLE;  return 0; }
    if (strcmp(s, "alarm_bar") == 0)      { *out = W_ALARM_BAR;      return 0; }
    if (strcmp(s, "alarm_list") == 0)     { *out = W_ALARM_LIST;     return 0; }
    if (strcmp(s, "recipe_table") == 0)   { *out = W_RECIPE_TABLE;   return 0; }
    if (strcmp(s, "message") == 0)        { *out = W_MESSAGE;        return 0; }
    if (strcmp(s, "image") == 0)  { *out = W_IMAGE;  return 0; }
    if (strcmp(s, "checkbox") == 0) { *out = W_CHECKBOX; return 0; }
    if (strcmp(s, "spinner") == 0)  { *out = W_SPINNER;  return 0; }
    if (strcmp(s, "keyboard") == 0) { *out = W_KEYBOARD; return 0; }
    if (strcmp(s, "tabview") == 0)  { *out = W_TABVIEW;  return 0; }
    if (strcmp(s, "knob") == 0)     { *out = W_KNOB;     return 0; }
    return -1;
}

/* 控件依赖的工程能力（manifest.features 的取值）；无依赖返回 NULL。
 * 表由 tools/gen_schema.py 从 schema 的 requires 生成，不手写。 */
const char* schema_widget_requires(const char* type)
{
    size_t i, n = sizeof(SC_WIDGET_REQUIRES) / sizeof(SC_WIDGET_REQUIRES[0]);

    if (!type) return NULL;
    for (i = 0; i < n; i++) {
        if (strcmp(SC_WIDGET_REQUIRES[i].id, type) == 0)
            return SC_WIDGET_REQUIRES[i].feature;
    }
    return NULL;
}

/* 取控件 id（错误信息用）；缺失返回 "?" */

/* ── schema 白名单校验（契约 v1） ──
 * widgets_schema.h 由 tools/gen_schema.py 从 spec/widget-schema.json 生成。 */
static int arr_has(const char* const* arr, const char* key)
{
    if (!arr) return 0;
    for (int i = 0; arr[i]; i++)
        if (strcmp(arr[i], key) == 0) return 1;
    return 0;
}

static const char* const* props_of(const schema_props_t* tbl, size_t n, const char* id)
{
    for (size_t i = 0; i < n; i++)
        if (strcmp(tbl[i].id, id) == 0) return tbl[i].props;
    return NULL;
}

static int schema_check_props(const char* type, const hmi_json_t* props,
                              char* err, size_t errsz)
{
    const char* const* own = props_of(SC_WIDGETS,
                                      sizeof(SC_WIDGETS) / sizeof(SC_WIDGETS[0]), type);
    const char* const* fig = NULL;
    size_t n = hmi_json_count(props);

    if (!own) {
        snprintf(err, errsz, "类型 %s 不在 schema 里", type);
        return -1;
    }
    if (strcmp(type, "shape") == 0) {
        const char* kind = hmi_json_str(hmi_json_get(props, "kind"), "rect");

        fig = props_of(SC_FIGURES, sizeof(SC_FIGURES) / sizeof(SC_FIGURES[0]), kind);
        if (!fig) {
            snprintf(err, errsz, "图元 kind=%s 不在 schema 里", kind);
            return -1;
        }
    }
    for (size_t i = 0; i < n; i++) {
        const char* key = hmi_json_key_at(props, i);

        if (!key) continue;
        if (arr_has(SC_COMMON, key) || arr_has(own, key)) continue;
        if (fig && (arr_has(SC_FIGURE_COMMON, key) || arr_has(fig, key))) continue;
        snprintf(err, errsz,
                 "属性 %s 未在 schema 中定义（旧名已按 ADR-0006 失效，见 "
                 "spec/widget-schema.json）", key);
        return -1;
    }
    return 0;
}

/* ── states（ADR-0005）：按条件覆盖一组外观 ──
 * 条件三选一（变量位 / 变量值相等 / 表达式），覆盖项限定在下面 10 个键里；
 * 没命中任何状态就还原建控件时捕获的基础值（不是"保持最后一条"，否则会残留别处的样式）。 */

int widget_build(const hmi_json_t* w, widget_t* out, char* err, size_t errsz)
{
    const char* type_s = hmi_json_str(hmi_json_get(w, "type"), NULL);
    const hmi_json_t* props = hmi_json_get(w, "props");
    wtype_t type;
    long x, y, ww, hh;
    int rc;

    if (!type_s || widget_type_from_str(type_s, &type) != 0) {
        snprintf(err, errsz, "控件 %s: 类型 %s 暂未支持（v1：label/value/button/lamp）",
                 widget_id(w), type_s ? type_s : "(缺失)");
        return -1;
    }
    if (hmi_json_type(props) != HMI_JSON_OBJ) {
        snprintf(err, errsz, "控件 %s: 缺 props 对象", widget_id(w));
        return -1;
    }
    if (widget_rect(w, &x, &y, &ww, &hh) != 0) {
        snprintf(err, errsz, "控件 %s: x/y/w/h 非法或超出屏幕 %dx%d",
                 widget_id(w), HMI_LCD_WIDTH, HMI_LCD_HEIGHT);
        return -1;
    }

    /* 属性名必须在 spec/widget-schema.json 里定义过（契约 v1，见 ADR-0004/0006）。
     * 白名单由 tools/gen_schema.py 生成；写错名字或用了旧名在这里报错，而不是静默无效。 */
    if (schema_check_props(type_s, props, err, errsz) != 0) {
        char inner[160];

        snprintf(inner, sizeof(inner), "%s", err);
        snprintf(err, errsz, "控件 %s: %s", widget_id(w), inner);
        return -1;
    }

    memset(out, 0, sizeof(*out));
    out->cur_wstate = -1;          /* memset 给的是 0，会被当成"已经命中第 0 条"，导致首帧不应用 */
    out->lp_slot = -1;             /* 没配 long_press 事件（0 会被当成"第 0 个动作"） */
    out->bind_tag = -1;            /* 没绑变量：input_click_cb 靠 < 0 判断"点开键盘也没处写" */
    if (errsz) err[0] = '\0';      /* 让具体失败原因能带出去，别被兜底文案盖掉 */
    switch (type) {
    case W_LABEL:  rc = build_label(w, props, x, y, ww, hh, out);  break;
    case W_VALUE:  rc = build_value(props, x, y, ww, hh, out);     break;
    case W_BUTTON: rc = build_button(w, props, x, y, ww, hh, out); break;
    case W_LAMP:   rc = build_lamp(w, props, x, y, out);           break;
    case W_SWITCH: rc = build_switch(props, x, y, ww, hh, out);    break;
    case W_BAR:    rc = build_bar(props, x, y, ww, hh, out);       break;
    case W_GAUGE:  rc = build_gauge(props, x, y, ww, hh, out);     break;
    case W_SLIDER: rc = build_slider(props, x, y, ww, hh, out);    break;
    case W_ENUM_TEXT: rc = build_enum_text(w, props, x, y, ww, hh, out); break;
    case W_INPUT:  rc = build_input(props, x, y, ww, hh, out, err, errsz); break;
    case W_COMBOBOX: rc = build_combobox(props, x, y, ww, hh, out); break;
    case W_SHAPE:  rc = figure_build(props, x, y, ww, hh, out);    break;
    case W_STATE_IMAGE: rc = build_state_image(props, x, y, ww, hh, out, err, errsz); break;
    case W_TABLE:  rc = build_table(props, x, y, ww, hh, out, err, errsz); break;
    case W_QRCODE: rc = build_qrcode(props, x, y, ww, hh, out, err, errsz); break;
    case W_REALTIME_CURVE: rc = build_realtime_curve(props, x, y, ww, hh, out, err, errsz); break;
    case W_HISTORY_CURVE:  rc = build_curve(props, x, y, ww, hh, out, err, errsz, 1); break;
    case W_XY_CURVE:       rc = build_xy_curve(props, x, y, ww, hh, out, err, errsz); break;
    case W_CHART:          rc = chart_build(props, x, y, ww, hh, out, err, errsz); break;
    case W_ROLLER:         rc = build_roller(props, x, y, ww, hh, out); break;
    case W_ASCII_DISPLAY:  rc = build_ascii_display(props, x, y, ww, hh, out); break;
    case W_PIPE:           rc = build_pipe(props, x, y, ww, hh, out); break;
    case W_FRAME_ANIM:     rc = build_frame_anim(props, x, y, ww, hh, out, err, errsz); break;
    case W_HISTORY_TABLE:  rc = build_history_table(props, x, y, ww, hh, out, err, errsz); break;
    case W_ALARM_BAR:      rc = alarm_bar_build(props, x, y, ww, hh, out, err, errsz); break;
    case W_ALARM_LIST:     rc = alarm_list_build(props, x, y, ww, hh, out, err, errsz); break;
    case W_RECIPE_TABLE:   rc = recipe_table_build(props, x, y, ww, hh, out, err, errsz); break;
    case W_IMAGE:  rc = build_image(props, x, y, ww, hh, out, err, errsz); break;
    case W_MESSAGE: rc = build_message(props, x, y, ww, hh, out, err, errsz); break;
    case W_CHECKBOX: rc = build_checkbox(props, x, y, ww, hh, out); break;
    case W_SPINNER:  rc = build_spinner(props, x, y, ww, hh, out);  break;
    case W_KEYBOARD: rc = build_keyboard(props, x, y, ww, hh, out); break;
    case W_TABVIEW:  rc = build_tabview(props, x, y, ww, hh, out, err, errsz); break;
    case W_KNOB:     rc = build_knob(props, x, y, ww, hh, out); break;
    default:       rc = -1; break;
    }
    if (rc != 0) {
        if (!err[0])
            snprintf(err, errsz, "控件 %s: 属性不合法（见日志）", widget_id(w));
        if (out->obj) {
            lv_obj_delete(out->obj);
            out->obj = NULL;
        }
        return -1;
    }
    /* 旋转/镜像（ADR-0019）：按类型分三档，超出边界直接报错，不静默忽略 */
    if (apply_transform(out, props, ww, hh, widget_id(w), err, errsz) != 0) {
        if (out->obj) {
            lv_obj_delete(out->obj);
            out->obj = NULL;
        }
        return -1;
    }

    /* 通用外观（bg/border/radius/opacity/visible）统一在收口应用：每类各自硬编码的
     * 默认外观先落地，这里再按 props "配了才覆盖"。必须早于 states 解析 ——
     * states 捕获的是"基础外观"，晚了会把状态里的临时色当成基础值还原。 */
    apply_common_style(out, props);

    /* states 必须在控件建好之后解析：它要捕获此时的"基础外观"用于还原 */
    if (wstate_parse(out, props, err, errsz) != 0) {
        if (out->obj) {
            lv_obj_delete(out->obj);
            out->obj = NULL;
        }
        return -1;
    }
    if (out->nwstates) {
        wstate_capture(out);
        (void)ui_widget_states_apply(out);
    }
    return 0;
}
