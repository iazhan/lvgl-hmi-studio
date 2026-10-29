/*
 * widget_states.c — states（ADR-0005）：按条件覆盖一组外观
 */
#include "widgets_internal.h"
#include "ui_font.h"
#include "ui_image_bin.h"
#include "core/expr.h"
#include "core/i18n.h"
#include "hmi_log.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const char* const WSTATE_KEYS[] = {
    "text", "color", "bg_color", "border_color", "border_width",
    "radius", "image", "visible", "enabled", "opacity", NULL
};

static lv_obj_t* wstate_text_obj(widget_t* w)
{
    switch (w->type) {
    case W_BUTTON:    return w->aux;         /* 按钮文字在子标签里 */
    case W_LABEL:
    case W_VALUE:
    case W_ENUM_TEXT: return w->obj;
    default:          return NULL;
    }
}

void wstate_capture(widget_t* w)
{
    lv_obj_t* to;

    if (w->st_captured || !w->obj) return;
    to = wstate_text_obj(w);
    if (to) {
        const char* t = lv_label_get_text(to);

        snprintf(w->st_base.text, sizeof(w->st_base.text), "%s", t ? t : "");
        w->st_base.color = lv_obj_get_style_text_color(to, LV_PART_MAIN);
    }
    w->st_base.bg_color = (w->type == W_LAMP)
        ? lv_led_get_color(widget_paint_obj(w))           /* 灯珠色不在 style 里 */
        : lv_obj_get_style_bg_color(w->obj, LV_PART_MAIN);
    w->st_base.border_color = lv_obj_get_style_border_color(w->obj, LV_PART_MAIN);
    w->st_base.border_width = (int)lv_obj_get_style_border_width(w->obj, LV_PART_MAIN);
    w->st_base.radius = (int)lv_obj_get_style_radius(w->obj, LV_PART_MAIN);
    w->st_base.opacity = (int)lv_obj_get_style_opa(w->obj, LV_PART_MAIN);
    w->st_base.visible = !lv_obj_has_flag(w->obj, LV_OBJ_FLAG_HIDDEN);
    w->st_base.enabled = !lv_obj_has_state(w->obj, LV_STATE_DISABLED);
    w->st_captured = 1;
}

static int wstate_match(const widget_state_t* s)
{
    switch (s->kind) {
    case 0: {                       /* 变量位为 1 */
        int v = (int)tagdb_num(s->tag);

        return ((v >> s->bit) & 1) != 0;
    }
    case 1:                         /* 变量值相等 */
        return tagdb_num(s->tag) == s->eq;
    default: {                      /* 表达式非 0 */
        ex_val_t val;

        if (expr_eval(&s->expr, &val) != 0) return 0;
        /* 比较表达式的结果类型是 EX_V_BOOL，数值也在 n 里；字符串一律当假 */
        if (val.type == EX_V_STR) return 0;
        return val.n != 0.0;
    }
    }
}

static void wstate_apply(widget_t* w, const widget_state_t* s)
{
    lv_obj_t* to = wstate_text_obj(w);

    if (s->has_text && to) {
        char tb[160];

        /* 状态覆盖的文字与基础文字同口径：可写 @id，应用状态时渲染 */
        lv_label_set_text(to, i18n_text(s->text, tb, sizeof(tb)));
    }
    if (s->has_color) {
        if (to) lv_obj_set_style_text_color(to, s->color, LV_PART_MAIN);
        else lv_obj_set_style_text_color(w->obj, s->color, LV_PART_MAIN);
    }
    if (s->has_bg) {
        lv_obj_t* po = widget_paint_obj(w);

        if (w->type == W_LAMP) {
            lv_led_set_color(po, s->bg_color);      /* 灯珠颜色不在 style 里 */
        } else {
            lv_obj_set_style_bg_color(po, s->bg_color, LV_PART_MAIN);
            lv_obj_set_style_bg_opa(po, LV_OPA_COVER, LV_PART_MAIN);
        }
    }
    if (s->has_border_color)
        lv_obj_set_style_border_color(w->obj, s->border_color, LV_PART_MAIN);
    if (s->has_border_width)
        lv_obj_set_style_border_width(w->obj, s->border_width, LV_PART_MAIN);
    if (s->has_radius) lv_obj_set_style_radius(w->obj, s->radius, LV_PART_MAIN);
    if (s->has_opacity) lv_obj_set_style_opa(w->obj, (lv_opa_t)s->opacity, LV_PART_MAIN);
    if (s->has_visible) {
        if (s->visible) lv_obj_remove_flag(w->obj, LV_OBJ_FLAG_HIDDEN);
        else lv_obj_add_flag(w->obj, LV_OBJ_FLAG_HIDDEN);
    }
    if (s->has_enabled) {
        if (s->enabled) {
            lv_obj_remove_state(w->obj, LV_STATE_DISABLED);
            lv_obj_add_flag(w->obj, LV_OBJ_FLAG_CLICKABLE);
            lv_obj_set_style_opa(w->obj, LV_OPA_COVER, LV_PART_MAIN);
        } else {
            lv_obj_add_state(w->obj, LV_STATE_DISABLED);
            lv_obj_clear_flag(w->obj, LV_OBJ_FLAG_CLICKABLE);
            if (w->off_hide) lv_obj_add_flag(w->obj, LV_OBJ_FLAG_HIDDEN);
            else lv_obj_set_style_opa(w->obj, LV_OPA_50, LV_PART_MAIN);
        }
    }
    if (s->has_image) {
        char path[320], ierr[160];

        /* 只有图片族有 dsc/img_buf（族字段在 union 里） */
        if (w->type != W_IMAGE && w->type != W_STATE_IMAGE && w->type != W_FRAME_ANIM) {
            HMI_LOG("states: 控件类型 %d 不支持 states.image（只有图片类控件能换图）", (int)w->type);
            return;
        }
        if (image_bin_path(ui_asset_root, s->image, path, sizeof(path)) != 0 ||
            image_bin_load(path, &w->u.img.dsc, &w->u.img.img_buf, ierr, sizeof(ierr)) != 0) {
            HMI_LOG("states: 图片 %s 加载失败：%s", s->image, ierr);
        } else {
            lv_image_set_src(w->obj, &w->u.img.dsc);
        }
    }
}

static void wstate_restore(widget_t* w)
{
    lv_obj_t* to = wstate_text_obj(w);

    if (to) {
        lv_label_set_text(to, w->st_base.text);
        lv_obj_set_style_text_color(to, w->st_base.color, LV_PART_MAIN);
    }
    if (w->type == W_LAMP) lv_led_set_color(widget_paint_obj(w), w->st_base.bg_color);
    else lv_obj_set_style_bg_color(w->obj, w->st_base.bg_color, LV_PART_MAIN);
    lv_obj_set_style_border_color(w->obj, w->st_base.border_color, LV_PART_MAIN);
    lv_obj_set_style_border_width(w->obj, w->st_base.border_width, LV_PART_MAIN);
    lv_obj_set_style_radius(w->obj, w->st_base.radius, LV_PART_MAIN);
    lv_obj_set_style_opa(w->obj, (lv_opa_t)w->st_base.opacity, LV_PART_MAIN);
    if (w->st_base.visible) lv_obj_remove_flag(w->obj, LV_OBJ_FLAG_HIDDEN);
    else lv_obj_add_flag(w->obj, LV_OBJ_FLAG_HIDDEN);
    if (w->st_base.enabled) {
        lv_obj_remove_state(w->obj, LV_STATE_DISABLED);
        lv_obj_add_flag(w->obj, LV_OBJ_FLAG_CLICKABLE);
    } else {
        lv_obj_add_state(w->obj, LV_STATE_DISABLED);
        lv_obj_clear_flag(w->obj, LV_OBJ_FLAG_CLICKABLE);
    }
}

int ui_widget_states_apply(widget_t* w)
{
    int hit = -1;

    if (!w || !w->nwstates || !w->obj) return 0;
    for (int i = 0; i < w->nwstates; i++) {
        if (wstate_match(&w->wstates[i])) {
            hit = i;
            break;                   /* 按声明顺序，第一条命中的生效 */
        }
    }
    if (hit == w->cur_wstate) return 0;
    w->cur_wstate = hit;
    if (hit < 0) wstate_restore(w);
    else wstate_apply(w, &w->wstates[hit]);
    return 1;
}

int wstate_parse(widget_t* w, const hmi_json_t* props, char* err, size_t errsz)
{
    const hmi_json_t* arr = hmi_json_get(props, "states");
    size_t n;

    if (hmi_json_type(arr) != HMI_JSON_ARR) return 0;
    n = hmi_json_count(arr);
    if (n == 0 || n > 8) {
        snprintf(err, errsz, "states 项数 %zu 越界（1..8）", n);
        return -1;
    }
    w->wstates = calloc(n, sizeof(*w->wstates));
    if (!w->wstates) {
        snprintf(err, errsz, "states 分配失败");
        return -1;
    }
    for (size_t i = 0; i < n; i++) {
        const hmi_json_t* it = hmi_json_at(arr, i);
        const hmi_json_t* when = hmi_json_get(it, "when");
        const hmi_json_t* ov = hmi_json_get(it, "props");
        widget_state_t* s = &w->wstates[w->nwstates];
        const hmi_json_t* bit = hmi_json_get(when, "bit");
        const hmi_json_t* eqv = hmi_json_get(when, "eq");
        const char* ex = hmi_json_str(hmi_json_get(when, "expr"), NULL);

        if (hmi_json_type(when) != HMI_JSON_OBJ) {
            snprintf(err, errsz, "states[%zu] 缺 when 对象", i);
            return -1;
        }
        if (hmi_json_type(bit) == HMI_JSON_ARR && hmi_json_count(bit) == 2) {
            s->kind = 0;
            s->tag = (int)hmi_json_int(hmi_json_at(bit, 0), -1);
            s->bit = (int)hmi_json_int(hmi_json_at(bit, 1), 0);
            if (!tagdb_valid(s->tag) || s->bit < 0 || s->bit > 31) {
                snprintf(err, errsz, "states[%zu] 的 bit 条件非法", i);
                return -1;
            }
        } else if (hmi_json_type(eqv) == HMI_JSON_ARR && hmi_json_count(eqv) == 2) {
            s->kind = 1;
            s->tag = (int)hmi_json_int(hmi_json_at(eqv, 0), -1);
            s->eq = hmi_json_num(hmi_json_at(eqv, 1), 0);
            if (!tagdb_valid(s->tag)) {
                snprintf(err, errsz, "states[%zu] 的 eq 条件非法", i);
                return -1;
            }
        } else if (ex && *ex) {
            char eerr[160];

            s->kind = 2;
            if (expr_compile(ex, &s->expr, eerr, sizeof(eerr)) != 0) {
                snprintf(err, errsz, "states[%zu] 的 expr 编译失败：%s", i, eerr);
                return -1;
            }
            s->has_expr = 1;
        } else {
            snprintf(err, errsz, "states[%zu] 缺触发条件（bit / eq / expr 三选一）", i);
            return -1;
        }

        if (hmi_json_type(ov) == HMI_JSON_OBJ) {
            size_t nk = hmi_json_count(ov);

            for (size_t k = 0; k < nk; k++) {
                const char* key = hmi_json_key_at(ov, k);
                const hmi_json_t* v = hmi_json_at(ov, k);
                int known = 0;

                for (int z = 0; WSTATE_KEYS[z]; z++)
                    if (key && strcmp(WSTATE_KEYS[z], key) == 0) { known = 1; break; }
                if (!known) {
                    snprintf(err, errsz, "states[%zu].props 里的 %s 不能覆盖", i, key);
                    return -1;
                }
                if (strcmp(key, "text") == 0) {
                    const char* t = hmi_json_str(v, "");

                    s->has_text = 1;
                    snprintf(s->text, sizeof(s->text), "%s", t);
                } else if (strcmp(key, "color") == 0) {
                    s->has_color = 1;
                    s->color = ui_color_hex(hmi_json_str(v, "#FFFFFF"));
                } else if (strcmp(key, "bg_color") == 0) {
                    s->has_bg = 1;
                    s->bg_color = ui_color_hex(hmi_json_str(v, "#000000"));
                } else if (strcmp(key, "border_color") == 0) {
                    s->has_border_color = 1;
                    s->border_color = ui_color_hex(hmi_json_str(v, "#000000"));
                } else if (strcmp(key, "border_width") == 0) {
                    s->has_border_width = 1;
                    s->border_width = (int)hmi_json_int(v, 1);
                } else if (strcmp(key, "radius") == 0) {
                    s->has_radius = 1;
                    s->radius = (int)hmi_json_int(v, 0);
                } else if (strcmp(key, "image") == 0) {
                    s->has_image = 1;
                    snprintf(s->image, sizeof(s->image), "%s", hmi_json_str(v, ""));
                } else if (strcmp(key, "visible") == 0) {
                    s->has_visible = 1;
                    s->visible = hmi_json_bool(v, 1);
                } else if (strcmp(key, "enabled") == 0) {
                    s->has_enabled = 1;
                    s->enabled = hmi_json_bool(v, 1);
                } else if (strcmp(key, "opacity") == 0) {
                    s->has_opacity = 1;
                    s->opacity = (int)hmi_json_int(v, 255);
                }
            }
        }
        w->nwstates++;
    }
    return 0;
}
