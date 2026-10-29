/*
 * widget_image.c — 位图与动画：image / state_image / frame_anim / qrcode
 */
#include "widgets_internal.h"
#include "ui_font.h"
#include "ui_image_bin.h"
#include "hmi_log.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int build_image(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out, char* err, size_t errsz)
{
    const char* src = hmi_json_str(hmi_json_get(props, "src"), NULL);
    char path[400], why[160];
    lv_obj_t* o;

    if (!src || !*src) {
        snprintf(err, errsz, "缺 props.src");
        return -1;
    }
    if (image_bin_path(ui_asset_root, src, path, sizeof(path)) != 0) {
        snprintf(err, errsz, "src %s 不是工程内相对路径", src);
        return -1;
    }
    if (image_bin_load(path, &out->u.img.dsc, &out->u.img.img_buf, why, sizeof(why)) != 0) {
        snprintf(err, errsz, "%s", why);
        return -1;
    }
    o = lv_image_create(cur_parent());
    if (!o) {
        free(out->u.img.img_buf);
        out->u.img.img_buf = NULL;
        snprintf(err, errsz, "建图片控件失败");
        return -1;
    }
    lv_image_set_src(o, &out->u.img.dsc);
    lv_obj_set_pos(o, (int32_t)x, (int32_t)y);
    /* scale=actual 保持图片原始像素尺寸（不做拉伸）；缺省 fit 按框缩放。
     * 导出器按 StretchMode 写这两个值；面板上「拉伸方式」选「无」即 actual。 */
    if (strcmp(hmi_json_str(hmi_json_get(props, "scale"), "fit"), "actual") == 0) {
        lv_obj_set_size(o, (int32_t)out->u.img.dsc.header.w,
                        (int32_t)out->u.img.dsc.header.h);
    } else if (ww > 0 && hh > 0) {
        lv_obj_set_size(o, (int32_t)ww, (int32_t)hh);
    }
    out->obj = o;
    out->type = W_IMAGE;
    return 0;
}

/* 所有控件共用的位置/尺寸/内边距：页面是绝对坐标，不留默认内边距 */
static int state_img_ensure(widget_t* w, int idx, char* err, size_t errsz)
{
    char path[400];

    if (idx < 0 || idx >= w->u.img.nstates) return -1;
    if (w->u.img.states[idx].loaded) return 0;
    if (image_bin_path(ui_asset_root, w->u.img.states[idx].src, path, sizeof(path)) != 0) {
        snprintf(err, errsz, "%s 不是工程内相对路径", w->u.img.states[idx].src);
        return -1;
    }
    if (image_bin_load(path, &w->u.img.states[idx].dsc, &w->u.img.states[idx].buf,
                       err, errsz) != 0) {
        return -1;
    }
    w->u.img.states[idx].loaded = 1;
    return 0;
}

int ui_state_image_select(widget_t* w, double v)
{
    char err[160];

    if (!w || !w->u.img.states || w->obj == NULL) return -1;
    for (int i = 0; i < w->u.img.nstates; i++) {
        if (w->u.img.states[i].v != v) continue;
        if (i == w->u.img.cur_state) return 0;          /* 已经是这张，不重复设 */
        if (state_img_ensure(w, i, err, sizeof(err)) != 0) {
            HMI_LOG("state_image: %s", err);
            return -1;
        }
        lv_image_set_src(w->obj, &w->u.img.states[i].dsc);
        w->u.img.cur_state = i;
        return 0;
    }
    return -1;      /* 值不在映射表里：保留当前图，不猜 */
}

int build_state_image(const hmi_json_t* props, long x, long y, long ww, long hh,
                      widget_t* out, char* err, size_t errsz)
{
    const hmi_json_t* arr = hmi_json_get(props, "images");
    size_t n;
    lv_obj_t* o;

    if (hmi_json_type(arr) != HMI_JSON_ARR) {
        snprintf(err, errsz, "state_image 缺 props.images 数组");
        return -1;
    }
    n = hmi_json_count(arr);
    if (n == 0 || n > 16) {
        snprintf(err, errsz, "state_image 的 images 项数 %zu 超出 1..16", n);
        return -1;
    }
    out->u.img.states = calloc(n, sizeof(*out->u.img.states));
    if (!out->u.img.states) {
        snprintf(err, errsz, "state_image 内存不足");
        return -1;
    }
    out->u.img.nstates = (int)n;
    for (size_t i = 0; i < n; i++) {
        const hmi_json_t* it = hmi_json_at(arr, i);
        const char* src = hmi_json_str(hmi_json_get(it, "src"), NULL);
        double v = hmi_json_num(hmi_json_get(it, "v"), 0);

        if (!src || !*src) {
            snprintf(err, errsz, "state_image 的 images[%zu] 缺 src", i);
            return -1;
        }
        for (size_t k = 0; k < i; k++) {
            if (out->u.img.states[k].v == v) {
                snprintf(err, errsz, "state_image 的 images 值 %g 重复", v);
                return -1;
            }
        }
        out->u.img.states[i].v = v;
        snprintf(out->u.img.states[i].src, sizeof(out->u.img.states[i].src), "%s", src);
    }

    o = lv_image_create(cur_parent());
    if (!o) {
        snprintf(err, errsz, "state_image 建控件失败");
        return -1;
    }
    lv_obj_set_pos(o, (int32_t)x, (int32_t)y);
    if (ww > 0 && hh > 0) lv_obj_set_size(o, (int32_t)ww, (int32_t)hh);
    out->obj = o;
    out->type = W_STATE_IMAGE;
    out->u.img.cur_state = -1;

    /* 先放第一张，绑定第一次刷新会按实际值切过去 */
    if (state_img_ensure(out, 0, err, errsz) != 0) return -1;
    lv_image_set_src(o, &out->u.img.states[0].dsc);
    out->u.img.cur_state = 0;
    return 0;
}

/* ── table：只读自由表格（37 §6.5） ──
 * 不用 lv_table：它没有逐格样式，表头行会跟数据行一个颜色。这里用「行容器 + 单元格
 * label」自己搭，表头行换底色，单元格 text 直接就是 label 的文本，绑定改它即可。 */
int ui_qrcode_set(widget_t* w, const char* text)
{
    if (!text) return -1;
    if (strlen(text) > 128) {
        HMI_LOG("qrcode: 文本超过 128 字符，截断");
    }
    if (lv_qrcode_update(w->obj, text, (uint32_t)strlen(text)) != LV_RESULT_OK) {
        HMI_LOG("qrcode: 文本 %s 放不进当前尺寸（内容太长）", text);
        return -1;
    }
    return 0;
}

/* ── qrcode：二维码（37 §6.5） ── */
int build_qrcode(const hmi_json_t* props, long x, long y, long ww, long hh,
                 widget_t* out, char* err, size_t errsz)
{
    const char* text = hmi_json_str(hmi_json_get(props, "text"), "");
    const char* dark = hmi_json_str(hmi_json_get(props, "dark_color"), "#000000");
    const char* light = hmi_json_str(hmi_json_get(props, "light_color"), "#FFFFFF");
    lv_obj_t* o;
    int size;

    if (!ui_color_valid(dark) || !ui_color_valid(light)) {
        snprintf(err, errsz, "qrcode 的 dark_color/light_color 不是 #RRGGBB");
        return -1;
    }
    size = (int)((ww < hh ? ww : hh));
    if (size < 32 || size > 480) {
        snprintf(err, errsz, "qrcode 尺寸 %d 越界（32..480）", size);
        return -1;
    }
    o = lv_qrcode_create(cur_parent());
    if (!o) {
        snprintf(err, errsz, "qrcode 建控件失败");
        return -1;
    }
    lv_obj_set_pos(o, (int32_t)x, (int32_t)y);
    lv_obj_set_size(o, (int32_t)size, (int32_t)size);
    lv_qrcode_set_size(o, size);
    lv_qrcode_set_dark_color(o, ui_color_hex(dark));
    lv_qrcode_set_light_color(o, ui_color_hex(light));
    out->obj = o;
    out->type = W_QRCODE;
    if (text && *text && ui_qrcode_set(out, text) != 0) {
        snprintf(err, errsz, "qrcode 的内容过长，放不进 %dpx", size);
        return -1;
    }
    return 0;
}

/* 画一条 1px 的线（横向或纵向），用作网格/轴线/刻度 */
static void frame_anim_tick_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);

    if (!w || !w->obj || w->u.img.nstates <= 0) return;
    /* 有 bind.running 且当前为 0：停下并回到第一帧（设备停了，风机不该继续转） */
    if (w->gate_tag >= 0 && tagdb_num(w->gate_tag) == 0) {
        if (w->u.img.anim_idx != 0) {
            w->u.img.anim_idx = 0;
            lv_image_set_src(w->obj, &w->u.img.states[0].dsc);
        }
        return;
    }
    if (w->u.img.anim_idx + 1 >= w->u.img.nstates) {
        if (!w->u.img.anim_loop) {                 /* 单次播放：停在最后一帧 */
            lv_timer_delete(t);
            w->u.img.anim_timer = NULL;
            return;
        }
        w->u.img.anim_idx = 0;
    } else {
        w->u.img.anim_idx++;
    }
    lv_image_set_src(w->obj, &w->u.img.states[w->u.img.anim_idx].dsc);
}

int build_frame_anim(const hmi_json_t* props, long x, long y, long ww, long hh,
                     widget_t* out, char* err, size_t errsz)
{
    const hmi_json_t* arr = hmi_json_get(props, "frames");
    int interval = (int)hmi_json_int(hmi_json_get(props, "interval_ms"), 120);
    int loop = hmi_json_bool(hmi_json_get(props, "loop"), 1);
    size_t n;
    lv_obj_t* o;

    if (hmi_json_type(arr) != HMI_JSON_ARR) {
        snprintf(err, errsz, "frame_anim 缺 props.frames 数组");
        return -1;
    }
    n = hmi_json_count(arr);
    if (n < 2 || n > 32) {
        snprintf(err, errsz, "frame_anim 的 frames 项数 %zu 超出 2..32", n);
        return -1;
    }
    if (interval < 40) interval = 40;
    if (interval > 5000) interval = 5000;

    out->u.img.states = calloc(n, sizeof(*out->u.img.states));
    if (!out->u.img.states) {
        snprintf(err, errsz, "frame_anim 内存不足");
        return -1;
    }
    out->u.img.nstates = (int)n;
    for (size_t i = 0; i < n; i++) {
        const char* src = hmi_json_str(hmi_json_at(arr, i), NULL);

        if (!src || !*src) {
            snprintf(err, errsz, "frame_anim 的 frames[%zu] 不是字符串", i);
            return -1;
        }
        snprintf(out->u.img.states[i].src, sizeof(out->u.img.states[i].src), "%s", src);
        out->u.img.states[i].v = (double)i;
        if (state_img_ensure(out, (int)i, err, errsz) != 0) return -1;
    }

    o = lv_image_create(cur_parent());
    if (!o) {
        snprintf(err, errsz, "frame_anim 建控件失败");
        return -1;
    }
    lv_obj_set_pos(o, (int32_t)x, (int32_t)y);
    if (ww > 0 && hh > 0) lv_obj_set_size(o, (int32_t)ww, (int32_t)hh);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    out->obj = o;
    out->type = W_FRAME_ANIM;
    out->u.img.anim_loop = loop;
    out->u.img.anim_idx = 0;
    out->gate_tag = -1;
    lv_image_set_src(o, &out->u.img.states[0].dsc);
    out->u.img.anim_timer = lv_timer_create(frame_anim_tick_cb, (uint32_t)interval, out);
    if (!out->u.img.anim_timer) {
        snprintf(err, errsz, "frame_anim 定时器创建失败");
        return -1;
    }
    return 0;
}
