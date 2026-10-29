/*
 * ui_anim.c — 属性动画：把 animations[] 映射成 lv_anim
 */
#include "ui_anim.h"
#include "core/expr.h"
#include "hmi_log.h"

#include <stdio.h>
#include <string.h>

typedef enum {
    AN_X = 0, AN_Y, AN_W, AN_H, AN_OPACITY, AN_BG_COLOR, AN_COLOR,
    AN_RADIUS, AN_BORDER_W, AN_SCALE
} anim_prop_t;

typedef enum {
    TR_LOAD = 0, TR_CLICK, TR_PRESS, TR_CHANGE, TR_LONG_PRESS, TR_EXPR
} anim_trigger_t;

typedef struct {
    widget_t*   w;
    int         prop;
    int         trigger;
    int         dur_ms, delay_ms, repeat, path;
    int         hold;          /* 1 = 结束后保持终值（不还原） */
    double      from, to;
    lv_color_t  cfrom, cto;
    int         has_expr;
    expr_prog_t expr;
    int         expr_last;
    int         running;       /* 正在跑：被打断时不再重captured（否则还原点会取到中途值） */
    /* 还原用：建页时记下的原值 */
    double      orig;
    lv_color_t  corig;
} ui_anim_t;

static ui_anim_t s_anims[UI_ANIM_MAX];
static int       s_nanims;

static const struct {
    const char* name;
    int         prop;
} s_props[] = {
    { "x", AN_X }, { "y", AN_Y }, { "w", AN_W }, { "h", AN_H },
    { "opacity", AN_OPACITY }, { "bg_color", AN_BG_COLOR }, { "color", AN_COLOR },
    { "radius", AN_RADIUS }, { "border_width", AN_BORDER_W }, { "scale", AN_SCALE },
};

static int prop_of(const char* s)
{
    for (size_t i = 0; i < sizeof(s_props) / sizeof(s_props[0]); i++)
        if (strcmp(s_props[i].name, s) == 0) return s_props[i].prop;
    return -1;
}

static int prop_is_color(int p)
{
    return p == AN_BG_COLOR || p == AN_COLOR;
}

/* ── 取值 / 还原 ── */

static double prop_get(ui_anim_t* an)
{
    lv_obj_t* o = an->w->obj;

    switch (an->prop) {
    case AN_X: return (double)lv_obj_get_x(o);
    case AN_Y: return (double)lv_obj_get_y(o);
    case AN_W: return (double)lv_obj_get_width(o);
    case AN_H: return (double)lv_obj_get_height(o);
    case AN_OPACITY: return (double)lv_obj_get_style_opa(o, 0);
    case AN_RADIUS: return (double)lv_obj_get_style_radius(o, 0);
    case AN_BORDER_W: return (double)lv_obj_get_style_border_width(o, 0);
    case AN_SCALE: return (double)lv_image_get_scale(o);
    default: return 0.0;
    }
}

static void prop_set(ui_anim_t* an, double v)
{
    lv_obj_t* o = an->w->obj;
    int32_t i = (int32_t)v;

    switch (an->prop) {
    case AN_X: lv_obj_set_x(o, i); break;
    case AN_Y: lv_obj_set_y(o, i); break;
    case AN_W: lv_obj_set_width(o, i); break;
    case AN_H: lv_obj_set_height(o, i); break;
    case AN_OPACITY: lv_obj_set_style_opa(o, (lv_opa_t)i, 0); break;
    case AN_RADIUS: lv_obj_set_style_radius(o, i, 0); break;
    case AN_BORDER_W: lv_obj_set_style_border_width(o, i, 0); break;
    case AN_SCALE: lv_image_set_scale(o, (uint32_t)i); break;
    default: break;
    }
}

/* lamp 是 lv_led：它的颜色存在自己的 led->color 里，只改 style 的 bg_color 不生效
 * （绘制时用 led->color 混亮度）。所以灯要走 lv_led_set_color。 */
static void set_bg_color(lv_obj_t* o, lv_color_t c)
{
    if (lv_obj_check_type(o, &lv_led_class)) lv_led_set_color(o, c);
    else lv_obj_set_style_bg_color(o, c, 0);
}

static lv_color_t get_bg_color(lv_obj_t* o)
{
    if (lv_obj_check_type(o, &lv_led_class)) return lv_led_get_color(o);
    return lv_obj_get_style_bg_color(o, 0);
}

static void prop_restore(ui_anim_t* an)
{
    if (!an->w || !an->w->obj) return;
    if (prop_is_color(an->prop)) {
        if (an->prop == AN_BG_COLOR)
            set_bg_color(an->w->obj, an->corig);
        else
            lv_obj_set_style_text_color(an->w->obj, an->corig, 0);
    } else {
        prop_set(an, an->orig);
    }
}

/* ── 动画执行 ── */

static void anim_exec(void* var, int32_t v)
{
    ui_anim_t* an = (ui_anim_t*)var;

    if (!an->w || !an->w->obj) return;
    if (prop_is_color(an->prop)) {
        /* lv_color_mix(c1, c2, mix)：mix=255 → c1、mix=0 → c2（与 lighten/darken 一致），
         * 所以"从 cfrom 渐变到 cto"要写成 mix(cto, cfrom, t) */
        lv_color_t c = lv_color_mix(an->cto, an->cfrom, (uint8_t)v);

        if (an->prop == AN_BG_COLOR) set_bg_color(an->w->obj, c);
        else lv_obj_set_style_text_color(an->w->obj, c, 0);
    } else {
        prop_set(an, (double)v);
    }
}

static void anim_done_cb(lv_anim_t* a)
{
    ui_anim_t* an = (ui_anim_t*)a->var;

    if (!an) return;
    an->running = 0;
    /* 默认还原（anim 是瞬时的，states/bind 才拥有属性的长期值）；
     * hold=true 时停在终值，给"淡出后保持""变色后保持"这类一次性效果用。 */
    if (!an->hold) prop_restore(an);
    if (prop_is_color(an->prop)) {
        lv_color_t c = (an->prop == AN_BG_COLOR)
                           ? get_bg_color(an->w->obj)
                           : lv_obj_get_style_text_color(an->w->obj, 0);

        HMI_LOG("anim: #%d 结束（%s）颜色=%06x", (int)(an - s_anims),
                an->hold ? "保持终值" : "属性已还原",
                (unsigned)((c.red << 16) | (c.green << 8) | c.blue));
    } else {
        HMI_LOG("anim: #%d 结束（%s）", (int)(an - s_anims),
                an->hold ? "保持终值" : "属性已还原");
    }
}

static void anim_start_idx(int idx)
{
    ui_anim_t* an;
    lv_anim_t a;

    if (idx < 0 || idx >= s_nanims) return;
    an = &s_anims[idx];
    if (!an->w || !an->w->obj) return;

    /* 还原点必须在动画真正开始时取：建页期控件的 coords 还没算出来，
     * lv_obj_get_x() 拿到的是 0（位置类动画还原后会跳到左上角）。 */
    if (!an->running) {
        if (prop_is_color(an->prop))
            an->corig = (an->prop == AN_BG_COLOR) ? get_bg_color(an->w->obj)
                                                  : lv_obj_get_style_text_color(an->w->obj, 0);
        else
            an->orig = prop_get(an);
    }
    an->running = 1;
    lv_anim_delete(an->w->obj, anim_exec);      /* 打断上一次（同一控件同一执行体） */
    lv_anim_init(&a);
    lv_anim_set_var(&a, an);
    lv_anim_set_exec_cb(&a, anim_exec);
    lv_anim_set_duration(&a, (uint32_t)an->dur_ms);
    lv_anim_set_delay(&a, (uint32_t)an->delay_ms);
    lv_anim_set_repeat_count(&a, (uint32_t)an->repeat);
    lv_anim_set_completed_cb(&a, anim_done_cb);
    if (prop_is_color(an->prop)) lv_anim_set_values(&a, 0, 255);
    else lv_anim_set_values(&a, (int32_t)an->from, (int32_t)an->to);
    lv_anim_set_path_cb(&a, an->path == 1 ? lv_anim_path_step
                        : an->path == 2 ? lv_anim_path_ease_in
                        : an->path == 3 ? lv_anim_path_ease_out
                        : an->path == 4 ? lv_anim_path_ease_in_out
                        : an->path == 5 ? lv_anim_path_overshoot
                                        : lv_anim_path_linear);
    lv_anim_start(&a);
    HMI_LOG("anim: #%d 启动（%s，%dms%s）", idx,
            prop_is_color(an->prop) ? "颜色" : "数值", an->dur_ms,
            an->trigger == TR_EXPR ? "，表达式触发" : "");
}

static void anim_ev_cb(lv_event_t* e)
{
    int idx = (int)(intptr_t)lv_event_get_user_data(e);

    anim_start_idx(idx);
}

/* ── 建表 ── */

int ui_anim_build(const hmi_json_t* arr, widget_t* ws,
                  const hmi_json_t* const* nodes, int n,
                  char* err, size_t errsz)
{
    size_t cnt;

    s_nanims = 0;
    memset(s_anims, 0, sizeof(s_anims));
    if (hmi_json_type(arr) != HMI_JSON_ARR) return 0;
    cnt = hmi_json_count(arr);
    if (cnt > UI_ANIM_MAX) {
        snprintf(err, errsz, "animations 有 %zu 条，超过每页上限 %d", cnt, UI_ANIM_MAX);
        return -1;
    }

    for (size_t i = 0; i < cnt; i++) {
        const hmi_json_t* a = hmi_json_at(arr, i);
        const char* target = hmi_json_str(hmi_json_get(a, "target"), NULL);
        const char* prop = hmi_json_str(hmi_json_get(a, "prop"), NULL);
        const char* path = hmi_json_str(hmi_json_get(a, "path"), NULL);
        const hmi_json_t* trig = hmi_json_get(a, "trigger");
        int slot = -1, p;
        ui_anim_t* an;

        if (!target || !prop) {
            snprintf(err, errsz, "animations[%zu]: 缺 target 或 prop", i);
            return -1;
        }
        p = prop_of(prop);
        if (p < 0) {
            snprintf(err, errsz,
                     "animations[%zu]: prop %s 不在白名单"
                     "（x/y/w/h/opacity/bg_color/color/radius/border_width/scale）", i, prop);
            return -1;
        }
        for (int k = 0; k < n; k++) {
            const char* id = hmi_json_str(hmi_json_get(nodes[k], "id"), NULL);

            if (id && strcmp(id, target) == 0) {
                slot = k;
                break;
            }
        }
        if (slot < 0) {
            snprintf(err, errsz, "animations[%zu]: target %s 不存在", i, target);
            return -1;
        }
        if (p == AN_SCALE && ws[slot].type != W_IMAGE) {
            snprintf(err, errsz, "animations[%zu]: scale 只对 image 有效（%s 是别的类型）",
                     i, target);
            return -1;
        }
        /* 同一控件同一属性只允许一条：两条同时跑会互相覆盖，还原值也没法定义 */
        for (int k = 0; k < s_nanims; k++) {
            if (s_anims[k].w == &ws[slot] && s_anims[k].prop == p) {
                snprintf(err, errsz,
                         "animations[%zu]: %s 的 %s 已经有动画了（同一属性只允许一条）",
                         i, target, prop);
                return -1;
            }
        }

        an = &s_anims[s_nanims];
        an->w = &ws[slot];
        an->prop = p;
        an->dur_ms = (int)hmi_json_int(hmi_json_get(a, "dur_ms"), 300);
        an->delay_ms = (int)hmi_json_int(hmi_json_get(a, "delay_ms"), 0);
        an->repeat = (int)hmi_json_int(hmi_json_get(a, "repeat"), 0);
        an->hold = hmi_json_bool(hmi_json_get(a, "hold"), 0);
        an->path = 0;
        if (path) {
            if (strcmp(path, "step") == 0) an->path = 1;
            else if (strcmp(path, "ease_in") == 0) an->path = 2;
            else if (strcmp(path, "ease_out") == 0) an->path = 3;
            else if (strcmp(path, "ease_in_out") == 0) an->path = 4;
            else if (strcmp(path, "overshoot") == 0) an->path = 5;
        }
        if (an->dur_ms < 1 || an->dur_ms > 10000) {
            snprintf(err, errsz, "animations[%zu]: dur_ms %d 越界（1..10000）", i, an->dur_ms);
            return -1;
        }
        if (an->delay_ms < 0 || an->delay_ms > 10000) {
            snprintf(err, errsz, "animations[%zu]: delay_ms %d 越界（0..10000）", i, an->delay_ms);
            return -1;
        }
        if (an->repeat < 0 || an->repeat > 1000) {
            snprintf(err, errsz, "animations[%zu]: repeat %d 越界（0..1000）", i, an->repeat);
            return -1;
        }

        if (prop_is_color(p)) {
            const char* cf = hmi_json_str(hmi_json_get(a, "from"), NULL);
            const char* ct = hmi_json_str(hmi_json_get(a, "to"), NULL);

            if (!cf || !ct || !ui_color_valid(cf) || !ui_color_valid(ct)) {
                snprintf(err, errsz, "animations[%zu]: %s 的 from/to 必须是 #RRGGBB", i, prop);
                return -1;
            }
            an->cfrom = ui_color_hex(cf);
            an->cto = ui_color_hex(ct);
            an->corig = (p == AN_BG_COLOR) ? get_bg_color(an->w->obj)
                                           : lv_obj_get_style_text_color(an->w->obj, 0);
        } else {
            an->from = hmi_json_num(hmi_json_get(a, "from"), 0);
            an->to = hmi_json_num(hmi_json_get(a, "to"), 0);
            an->orig = prop_get(an);
        }

        /* 触发器 */
        if (hmi_json_type(trig) == HMI_JSON_OBJ) {
            const char* es = hmi_json_str(hmi_json_get(trig, "expr"), NULL);
            char eerr[128];

            if (!es || !*es) {
                snprintf(err, errsz, "animations[%zu]: trigger 对象里缺 expr", i);
                return -1;
            }
            if (expr_compile(es, &an->expr, eerr, sizeof(eerr)) != 0) {
                snprintf(err, errsz, "animations[%zu]: trigger 表达式错误：%s", i, eerr);
                return -1;
            }
            an->has_expr = 1;
            an->trigger = TR_EXPR;
            an->expr_last = 0;
        } else {
            const char* ts = hmi_json_str(trig, "load");

            if (strcmp(ts, "load") == 0) an->trigger = TR_LOAD;
            else if (strcmp(ts, "click") == 0) an->trigger = TR_CLICK;
            else if (strcmp(ts, "press") == 0) an->trigger = TR_PRESS;
            else if (strcmp(ts, "change") == 0) an->trigger = TR_CHANGE;
            else if (strcmp(ts, "long_press") == 0) an->trigger = TR_LONG_PRESS;
            else {
                snprintf(err, errsz,
                         "animations[%zu]: trigger %s 不认识"
                         "（load/click/press/change/long_press 或 {expr}）", i, ts);
                return -1;
            }
            if (an->trigger != TR_LOAD) {
                lv_event_code_t code = an->trigger == TR_CLICK ? LV_EVENT_CLICKED
                                     : an->trigger == TR_PRESS ? LV_EVENT_PRESSED
                                     : an->trigger == TR_CHANGE ? LV_EVENT_VALUE_CHANGED
                                                                : LV_EVENT_LONG_PRESSED;

                lv_obj_add_event_cb(an->w->obj, anim_ev_cb, code,
                                    (void*)(intptr_t)s_nanims);
            }
        }
        s_nanims++;
    }
    if (s_nanims > 0) HMI_LOG("anim: 载入 %d 条动画", s_nanims);
    return 0;
}

void ui_anim_start_load(void)
{
    for (int i = 0; i < s_nanims; i++)
        if (s_anims[i].trigger == TR_LOAD) anim_start_idx(i);
}

void ui_anim_tick(void)
{
    for (int i = 0; i < s_nanims; i++) {
        ui_anim_t* an = &s_anims[i];
        ex_val_t v;
        int on;

        if (!an->has_expr || !an->w || !an->w->obj) continue;
        if (expr_eval(&an->expr, &v) != 0) continue;
        on = (v.type == EX_V_BOOL) ? (v.n != 0.0) : (v.n != 0.0);
        if (on && !an->expr_last) anim_start_idx(i);   /* 只在上升沿触发 */
        an->expr_last = on;
    }
}

void ui_anim_reset(void)
{
    for (int i = 0; i < s_nanims; i++) {
        if (s_anims[i].w && s_anims[i].w->obj) {
            lv_anim_delete(s_anims[i].w->obj, anim_exec);
            prop_restore(&s_anims[i]);
        }
        s_anims[i].running = 0;
        if (s_anims[i].has_expr) expr_free(&s_anims[i].expr);
    }
    memset(s_anims, 0, sizeof(s_anims));
    s_nanims = 0;
}
