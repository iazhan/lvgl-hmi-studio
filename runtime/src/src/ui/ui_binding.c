/*
 * ui_binding.c — 绑定表构建与刷新
 */
#include "ui_binding.h"
#include "widgets_internal.h"
#include "core/tagdb.h"
#include "core/i18n.h"
#include "hmi_log.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* fmt 转换族：int 族（%d %i %u %x %X %o %c）与 float 族（%f %e %g %a）。
 * 求值时按族选参数类型——把 int64_t 递给 %f 是未定义行为，必须在构建期分流。
 * 混族、无转换符、含 * 宽度（需要额外实参）都判为无效。 */
typedef enum { CONV_NONE = 0, CONV_INT, CONV_FLOAT } conv_family_t;

static conv_family_t fmt_family(const char* fmt)
{
    conv_family_t fam = CONV_NONE;

    for (const char* p = fmt; *p; p++) {
        if (*p != '%') continue;
        p++;
        if (*p == '%') continue;               /* %% 转义 */
        /* 跳过标志/宽度/精度修饰（不含 *：那需要调用方给实参，这里给不了） */
        while (*p == '-' || *p == '+' || *p == ' ' || *p == '#' || *p == '0' ||
               (*p >= '1' && *p <= '9') || *p == '.' ||
               *p == 'h' || *p == 'l' || *p == 'L' || *p == 'z' || *p == 'j' ||
               *p == 't')
            p++;
        if (!*p) return CONV_NONE;
        conv_family_t cur;
        if (strchr("diouxXc", *p)) cur = CONV_INT;
        else if (strchr("eEfFgGaA", *p)) cur = CONV_FLOAT;
        else return CONV_NONE;
        if (fam != CONV_NONE && fam != cur) return CONV_NONE;
        fam = cur;
    }
    return fam;
}

/* ── 构建 ── */

static int build_one_cells(const hmi_json_t* wj, widget_t* w, binding_t* e,
                           char* err, size_t errsz);

static int build_one(const hmi_json_t* wj, widget_t* w,
                     binding_t* e, char* err, size_t errsz)
{
    const hmi_json_t* bind = hmi_json_get(wj, "bind");
    const char* wid = hmi_json_str(hmi_json_get(wj, "id"), "?");
    size_t nprop;
    int n = 0;

    if (hmi_json_type(bind) != HMI_JSON_OBJ) return build_one_cells(wj, w, e, err, errsz);
    nprop = hmi_json_count(bind);

    for (size_t i = 0; i < nprop; i++) {
        const char* prop = hmi_json_key_at(bind, i);
        const hmi_json_t* b = hmi_json_at(bind, i);
        const char* kind;
        long tag;

        if (!prop) continue;
        /* running 是 frame_anim 的"播放开关"：由 page_mgr 取成 gate_tag（见 widgets.c 的
         * 动画定时器），不产生普通的可视绑定，所以这里校验完就跳过。 */
        if (strcmp(prop, "running") == 0) {
            long t = hmi_json_int(hmi_json_get(b, "tag"), -1);

            if (!tagdb_valid(t)) {
                snprintf(err, errsz, "控件 %s: bind.running 的 tag %ld 不存在", wid, t);
                return -1;
            }
            continue;
        }
        e->w = w;
        e->cell_r = -1;
        e->cell_c = -1;
        e->last_version = 0xFFFFFFFFu;    /* 首帧必刷 */

        if (strcmp(prop, "text") == 0) e->prop = BIND_TEXT;
        else if (strcmp(prop, "visible") == 0) e->prop = BIND_VISIBLE;
        /* 输入框没有"数值通道"：它的内容就是 textarea 里那段文字，`value` 与 `text`
         * 是同一件事（导出器对数值输入框发的正是 value）。走 TEXT 通路才能顺带带上
         * 前缀/单位/小数位，走 VALUE 通路则什么都不会发生。 */
        else if (strcmp(prop, "value") == 0)
            e->prop = (w->type == W_INPUT) ? BIND_TEXT : BIND_VALUE;
        else if (strcmp(prop, "checked") == 0) e->prop = BIND_CHECKED;
        /* selected 是 combobox 的"第几项"（契约 combobox.bind），语义与 value 一致：
         * 都是"按一个整数定位控件当前状态"，所以复用同一条刷新通路。 */
        else if (strcmp(prop, "selected") == 0) e->prop = BIND_VALUE;
        else if (strcmp(prop, "color") == 0) e->prop = BIND_COLOR;
        else if (strcmp(prop, "bg_color") == 0) e->prop = BIND_BG_COLOR;
        else if (strcmp(prop, "enabled") == 0) e->prop = BIND_ENABLED;
        else {
            snprintf(err, errsz,
                     "控件 %s: 绑定属性 %s 暂未支持"
                     "（v1：text/visible/value/checked/color/bg_color/enabled）",
                     wid, prop);
            return -1;
        }

        tag = hmi_json_int(hmi_json_get(b, "tag"), -1);
        /* 这里先不做结论：expr 不需要 tag，等 kind 解析完再判 */
        e->tag_id = tagdb_valid(tag) ? (int)tag : -1;

        kind = hmi_json_str(hmi_json_get(b, "kind"), NULL);
        if (kind && strcmp(kind, "enum") == 0) {
            const hmi_json_t* map = hmi_json_get(b, "map");
            size_t n;

            if (hmi_json_type(map) != HMI_JSON_ARR) {
                snprintf(err, errsz, "控件 %s: kind=enum 缺 map 数组", wid);
                return -1;
            }
            n = hmi_json_count(map);
            if (n == 0 || n > BIND_MAP_MAX) {
                snprintf(err, errsz, "控件 %s: enum 映射 %zu 项，超出 1..%d",
                         wid, n, BIND_MAP_MAX);
                return -1;
            }
            e->kind = BIND_ENUM;
            e->map_count = (int)n;
            for (size_t k = 0; k < n; k++) {
                const hmi_json_t* it = hmi_json_at(map, k);
                const char* txt = hmi_json_str(hmi_json_get(it, "text"), NULL);
                const char* col = hmi_json_str(hmi_json_get(it, "color"), "");

                if (!txt || !*txt || strlen(txt) >= sizeof(e->map[k].text)) {
                    snprintf(err, errsz,
                             "控件 %s: enum 映射第 %zu 项的 text 缺失或超过 %d 字节",
                             wid, k, (int)sizeof(e->map[k].text) - 1);
                    return -1;
                }
                if (*col && !ui_color_valid(col)) {
                    snprintf(err, errsz, "控件 %s: enum 映射第 %zu 项的 color 非法",
                             wid, k);
                    return -1;
                }
                e->map[k].v = hmi_json_num(hmi_json_get(it, "v"), 0);
                snprintf(e->map[k].text, sizeof(e->map[k].text), "%s", txt);
                snprintf(e->map[k].color, sizeof(e->map[k].color), "%s", col);
                for (int j = 0; j < (int)k; j++) {
                    if (e->map[j].v == e->map[k].v) {
                        snprintf(err, errsz, "控件 %s: enum 映射的 v=%.0f 重复",
                                 wid, e->map[k].v);
                        return -1;
                    }
                }
            }
        } else if (kind && strcmp(kind, "expr") == 0) {
            const char* src = hmi_json_str(hmi_json_get(b, "expr"), NULL);

            if (!src || !*src) {
                snprintf(err, errsz, "控件 %s: kind=expr 缺 expr 字符串", wid);
                return -1;
            }
            if (expr_compile(src, &e->prog, err, errsz) != 0) return -1;
            e->kind = BIND_EXPR;
        } else if (kind && strcmp(kind, "bit") == 0) {
            long bit = hmi_json_int(hmi_json_get(b, "bit"), -1);

            if (bit < 0 || bit > 63) {
                snprintf(err, errsz, "控件 %s: kind=bit 的 bit %ld 越界（0..63）",
                         wid, bit);
                return -1;
            }
            e->kind = BIND_BIT;
            e->bit = (int)bit;
        } else if (kind && strcmp(kind, "ranges") == 0) {
            const hmi_json_t* arr = hmi_json_get(b, "ranges");
            size_t n;

            if (hmi_json_type(arr) != HMI_JSON_ARR) {
                snprintf(err, errsz, "控件 %s: kind=ranges 缺 ranges 数组", wid);
                return -1;
            }
            n = hmi_json_count(arr);
            if (n == 0 || n > BIND_RANGE_MAX) {
                snprintf(err, errsz, "控件 %s: ranges %zu 项，超出 1..%d",
                         wid, n, BIND_RANGE_MAX);
                return -1;
            }
            e->kind = BIND_RANGES;
            e->range_count = (int)n;
            for (size_t k = 0; k < n; k++) {
                const hmi_json_t* it = hmi_json_at(arr, k);
                const char* col = hmi_json_str(hmi_json_get(it, "color"), NULL);
                const hmi_json_t* lt = hmi_json_get(it, "lt");
                int has_lt = (lt != NULL);

                if (!col || !ui_color_valid(col)) {
                    snprintf(err, errsz, "控件 %s: ranges 第 %zu 项的 color 非法",
                             wid, k);
                    return -1;
                }
                /* 只有最后一项可以不写 lt；中间项缺了就没法判定上界 */
                if (!has_lt && k + 1 != n) {
                    snprintf(err, errsz,
                             "控件 %s: ranges 第 %zu 项缺 lt（只有最后一项可省略）",
                             wid, k);
                    return -1;
                }
                e->ranges[k].has_lt = has_lt;
                e->ranges[k].lt = has_lt ? hmi_json_num(lt, 0) : 0;
                snprintf(e->ranges[k].color, sizeof(e->ranges[k].color), "%s", col);
            }
        } else if (kind && strcmp(kind, "fmt") == 0) {
            const char* fmt = hmi_json_str(hmi_json_get(b, "fmt"), NULL);
            conv_family_t fam;
            tag_type_t tt;

            if (!fmt || !*fmt) {
                snprintf(err, errsz, "控件 %s: kind=fmt 缺 fmt", wid);
                return -1;
            }
            if (strlen(fmt) >= sizeof(e->fmt)) {
                snprintf(err, errsz, "控件 %s: fmt 超过 %zu 字节", wid,
                         sizeof(e->fmt) - 1);
                return -1;
            }
            fam = fmt_family(fmt);
            if (fam == CONV_NONE) {
                snprintf(err, errsz, "控件 %s: fmt \"%s\" 没有有效转换说明符"
                                     "（或混用整型/浮点/含 * 宽度）", wid, fmt);
                return -1;
            }
            if (e->tag_id < 0) {
                snprintf(err, errsz, "控件 %s: kind=fmt 需要 tag", wid);
                return -1;
            }
            tt = tagdb_at(e->tag_id)->type;
            if (fam == CONV_FLOAT && (tt == TAG_T_BOOL || tt == TAG_T_I16 ||
                                      tt == TAG_T_U16)) {
                snprintf(err, errsz,
                         "控件 %s: fmt 含浮点转换，但 tag %d 是布尔/窄整数", wid,
                         e->tag_id);
                return -1;
            }
            if (e->prop == BIND_VISIBLE) {
                snprintf(err, errsz, "控件 %s: visible 绑定不支持 kind=fmt", wid);
                return -1;
            }
            snprintf(e->fmt, sizeof(e->fmt), "%s", fmt);
            e->kind = BIND_FMT;
        } else if (kind) {
            snprintf(err, errsz,
                     "控件 %s: 绑定 kind %s 暂未实现（v1：fmt / enum / 直接取值）",
                     wid, kind);
            return -1;
        } else {
            e->kind = BIND_DIRECT;
        }

        if (e->kind != BIND_EXPR && e->tag_id < 0) {
            snprintf(err, errsz, "控件 %s: 绑定的 tag %ld 不存在", wid, tag);
            return -1;
        }
        e++;
        n++;
    }
    return n;
}

int bind_table_build(binding_t** out, int* count, const hmi_json_t* const* nodes,
                     widget_t* widgets, int nwidgets, char* err, size_t errsz)
{
    binding_t* items;
    int total = 0;
    int used = 0;

    *out = NULL;
    *count = 0;
    if (nwidgets <= 0) return 0;

    items = calloc((size_t)nwidgets * 2, sizeof(*items));   /* 每控件最多绑 2 个属性 */
    if (!items) {
        snprintf(err, errsz, "绑定表内存不足");
        return -1;
    }

    for (int i = 0; i < nwidgets; i++) {
        const hmi_json_t* wj = nodes[i];

        if (!wj) continue;
        int n = build_one(wj, &widgets[i], items + used, err, errsz);
        if (n < 0) {
            free(items);
            return -1;
        }
        used += n;
        total += n;
    }

    *out = items;
    *count = total;
    return 0;
}

void bind_table_free(binding_t* items, int count)
{
    (void)count;
    free(items);
}

/* ── 求值 ── */

/* 求一次 expr。成功返回 0；失败（语法外的一切运行期错误）返回 -1，
 * 调用方保留上次的值——按规范不做"猜一个 0 出来"。 */
static int eval_expr(const binding_t* b, ex_val_t* out)
{
    if (expr_eval(&b->prog, out) != 0) {
        if (!b->expr_err_logged) {
            HMI_LOG("binding: expr 求值失败（除零/类型错/变量越界），保留上次的值");
            ((binding_t*)b)->expr_err_logged = 1;
        }
        return -1;
    }
    return 0;
}

/* 绑定的有效取值：kind=bit 时取某一位，其余就是变量值 */
static double bind_value(const binding_t* b)
{
    double v = (tagdb_at(b->tag_id)->type == TAG_T_BOOL)
                   ? (tagdb_bool(b->tag_id) ? 1.0 : 0.0)
                   : tagdb_num(b->tag_id);

    if (b->kind == BIND_BIT) v = (double)(((long long)v >> b->bit) & 1);
    return v;
}

/* 返回 0 表示 out 里有有效文字；-1 表示求值失败（调用方必须跳过写入，
 * 否则会把未初始化的缓冲区塞进控件） */
static int eval_text(const binding_t* b, char* out, size_t outsz)
{
    const tag_t* t;

    if (b->kind == BIND_EXPR) {
        ex_val_t v;

        if (eval_expr(b, &v) != 0) return -1;   /* 保留上次文字 */
        if (v.type == EX_V_STR) {
            snprintf(out, outsz, "%s", v.s ? v.s : "");
        } else if (v.type == EX_V_BOOL) {
            snprintf(out, outsz, "%d", v.n != 0 ? 1 : 0);
        } else {
            snprintf(out, outsz, "%.*f", b->w ? b->w->decimals : 0, v.n);
        }
        return 0;
    }

    t = tagdb_at(b->tag_id);
    if (!t) { snprintf(out, outsz, "-"); return 0; }

    /* 质量不是 GOOD 就显示 ---：断线、尚未取过值都属于「这个数不可信」，
     * 把上一次的旧值留在界面上比显示 --- 更危险。 */
    if (tagdb_quality(b->tag_id) != TAG_Q_GOOD) {
        snprintf(out, outsz, "---");
        return 0;
    }

    if (b->kind == BIND_ENUM) {
        double v = bind_value(b);

        for (int i = 0; i < b->map_count; i++) {
            if (b->map[i].v == v) {
                char tb[96];

                /* 映射文本可以是 @id（多语言）：取值处渲染，切语言重建页面即生效 */
                snprintf(out, outsz, "%s", i18n_text(b->map[i].text, tb, sizeof(tb)));
                return 0;
            }
        }
        /* 未命中不猜：映射表是工程声明的全部取值，出现别的值说明设备有问题 */
        snprintf(out, outsz, "-");
        return 0;
    }

    if (b->kind == BIND_FMT) {
        /* 字符串变量不套数值格式：导出器若给了 %.2f（或手写 JSON 是这么写的），
         * 会把字符串按数值渲染成 0.00，输入框/标签里就看不到真正的文本。 */
        if (t->type == TAG_T_STRING) {
            int n = tagdb_str_copy(b->tag_id, out, (int)outsz - 1, NULL);

            if (n < 0) n = 0;
            out[n] = '\0';
            return 0;
        }
        if (fmt_family(b->fmt) == CONV_FLOAT) {
            snprintf(out, outsz, b->fmt, bind_value(b));
        } else {
            long long v;

            if (b->kind == BIND_DIRECT && t->type == TAG_T_I64)
                v = (long long)tagdb_i64(b->tag_id);
            else
                v = (long long)bind_value(b);
            snprintf(out, outsz, b->fmt, v);
        }
        return 0;
    }

    /* 直接取值：数值按控件 decimals，布尔按 0/1，i64 按整数 */
    if (b->kind == BIND_BIT) {
        snprintf(out, outsz, "%.0f", bind_value(b));
    } else if (t->type == TAG_T_BOOL) {
        snprintf(out, outsz, "%d", tagdb_bool(b->tag_id) ? 1 : 0);
    } else if (t->type == TAG_T_I64) {
        snprintf(out, outsz, "%lld", (long long)tagdb_i64(b->tag_id));
    } else if (t->type == TAG_T_STRING) {
        /* 字符串变量按原样给（早先漏了这一支，会掉到 %.1f 变成 "0.0"） */
        int n = tagdb_str_copy(b->tag_id, out, (int)outsz - 1, NULL);

        if (n < 0) n = 0;
        out[n] = '\0';
    } else {
        int dec = (b->w && b->w->type == W_VALUE) ? b->w->decimals : 1;
        if (dec < 0) dec = 0;
        if (dec > 6) dec = 6;
        snprintf(out, outsz, "%.*f", dec, tagdb_num(b->tag_id));
    }
    return 0;
}

static int eval_visible(const binding_t* b)
{
    const tag_t* t = tagdb_at(b->tag_id);

    if (!t) return 0;
    if (b->kind == BIND_EXPR) {
        ex_val_t v;

        if (eval_expr(b, &v) != 0) return 0;
        if (v.type == EX_V_STR) return v.s && v.s[0];
        return v.n != 0;
    }
    if (b->kind == BIND_BIT || t->type != TAG_T_BOOL) return bind_value(b) != 0.0;
    return tagdb_bool(b->tag_id);
}

/* ── 应用 ── */

/* 按 enum 映射或 ranges 区间求当前颜色；没有匹配返回 NULL（保持原色） */
static const char* eval_color(const binding_t* b)
{
    double v;

    if (b->kind == BIND_EXPR) {
        ex_val_t ev;

        if (eval_expr(b, &ev) != 0) return NULL;
        v = (ev.type == EX_V_STR) ? 0 : ev.n;
    } else {
        v = bind_value(b);
    }

    if (b->kind == BIND_ENUM) {
        for (int i = 0; i < b->map_count; i++)
            if (b->map[i].v == v && b->map[i].color[0]) return b->map[i].color;
        return NULL;
    }
    if (b->kind == BIND_RANGES) {
        for (int i = 0; i < b->range_count; i++) {
            if (b->ranges[i].has_lt) {
                if (v < b->ranges[i].lt) return b->ranges[i].color;
            } else {
                return b->ranges[i].color;
            }
        }
    }
    return NULL;
}

static void apply_binding(const binding_t* b)
{
    if (!b->w || !b->w->obj) return;

    if (b->prop == BIND_TEXT) {
        char text[128] = "";

        if (eval_text(b, text, sizeof(text)) != 0) return;
        if (b->w && b->w->type == W_QRCODE) {
            /* 二维码按整段文本重画，不是普通 label */
            extern int ui_qrcode_set(widget_t* w, const char* text);

            (void)ui_qrcode_set(b->w, text);
            return;
        }
        if (b->w && b->w->type == W_MESSAGE) {
            /* 消息栏换文字要顺带重新计时（auto_hide_ms） */
            ui_message_set_text(b->w, text);
            return;
        }
        if (b->cell_r >= 0) {
            lv_obj_t* lb = b->w->u.table.cell[b->cell_r * 8 + b->cell_c];

            if (lb) lv_label_set_text(lb, text);
            return;
        }
        if (b->kind == BIND_ENUM) {
            double v = tagdb_num(b->tag_id);
            const char* col = NULL;

            for (int i = 0; i < b->map_count; i++) {
                if (b->map[i].v == v && b->map[i].color[0]) {
                    col = b->map[i].color;
                    break;
                }
            }
            if (col && b->w->type != W_INPUT)
                lv_obj_set_style_text_color(b->w->obj, ui_color_hex(col), 0);
        }
        if (b->w->type == W_CHECKBOX) {
            /* 勾选框的文字就在 checkbox 本体上（lv_checkbox_set_text），不在 aux 里 */
            lv_checkbox_set_text(b->w->obj, text);
            return;
        }
        if (b->w->type == W_BUTTON) {
            /* 按钮的文字在子标签里；直接对按钮调 lv_label_set_text 会写错对象。
             * 同时记下当前文字，按下态换过之后再松开时才知道恢复成什么。 */
            if (b->w->aux && !lv_obj_has_state(b->w->obj, LV_STATE_PRESSED))
                lv_label_set_text(b->w->aux, text);
            snprintf(b->w->u.btn.last_text, sizeof(b->w->u.btn.last_text), "%s", text);
            return;
        }
        if (b->w->type == W_VALUE) {
            /* 前缀 + 数值 +（单位）+ 后缀；契约 value.prefix / suffix */
            char full[192];

            if (b->w->unit[0])
                snprintf(full, sizeof(full), "%s%s %s%s",
                         b->w->prefix, text, b->w->unit, b->w->suffix);
            else
                snprintf(full, sizeof(full), "%s%s%s", b->w->prefix, text, b->w->suffix);
            lv_label_set_text(b->w->obj, full);
        } else if (b->w->type == W_INPUT && b->w->unit[0]) {
            char full[160];

            snprintf(full, sizeof(full), "%s %s", text, b->w->unit);
            lv_textarea_set_text(b->w->obj, full);
        } else if (b->w->type == W_INPUT) {
            lv_textarea_set_text(b->w->obj, text);
        } else if (b->w->type == W_LABEL || b->w->type == W_VALUE ||
                   b->w->type == W_ENUM_TEXT) {
            lv_label_set_text(b->w->obj, text);
        } else {
            /* 其余控件没有可直接改的文本：报一次，别把字符串塞进别的对象 */
            if (!((binding_t*)b)->text_err_logged) {
                HMI_LOG("binding: 控件类型 %d 不支持 text 绑定（已跳过）", (int)b->w->type);
                ((binding_t*)b)->text_err_logged = 1;
            }
        }
    } else if (b->prop == BIND_VISIBLE) {
        if (eval_visible(b))
            lv_obj_clear_flag(b->w->obj, LV_OBJ_FLAG_HIDDEN);
        else
            lv_obj_add_flag(b->w->obj, LV_OBJ_FLAG_HIDDEN);
    } else if (b->prop == BIND_VALUE) {
        /* 质量不好就打到量程下限：读数不可信时宁可空着，不留上一次的形状 */
        double v = (tagdb_quality(b->tag_id) == TAG_Q_GOOD)
                       ? tagdb_num(b->tag_id) : (double)b->w->min;

        switch (b->w->type) {
        case W_BAR:
            lv_bar_set_value(b->w->obj, (int32_t)v, LV_ANIM_OFF);
            if (b->w->aux) {
                char pct[16];
                int span = b->w->max - b->w->min;

                snprintf(pct, sizeof(pct), "%d%%",
                         span > 0 ? (int)((v - b->w->min) * 100 / span) : 0);
                lv_label_set_text(b->w->aux, pct);
            }
            break;
        case W_GAUGE:
            ui_gauge_set(b->w, v);
            break;
        case W_STATE_IMAGE:
            (void)ui_state_image_select(b->w, v);
            break;
        case W_SLIDER:
            lv_slider_set_value(b->w->obj, (int32_t)v, LV_ANIM_OFF);
            break;
        case W_KNOB:
            ui_knob_set(b->w, v);
            break;
        case W_ROLLER:
            if (v >= 0) lv_roller_set_selected(b->w->obj, (uint32_t)v, LV_ANIM_OFF);
            break;
        case W_PIPE:
            ui_pipe_set_flow(b->w, v != 0.0);
            break;
        case W_SHAPE:
            /* 只有 kind=arc 图元有进度段（契约 figures.arc 的 min/max）；
             * 其它图元上写 value 绑定是空操作，不报错也不改画面。 */
            ui_arc_set_value(b->w, v);
            break;
        case W_COMBOBOX:
            /* 契约 combobox.bind 的 selected：按变量值切选中项（越界钳到范围内，
             * 越界会让 LVGL 显示成空框）。options_tag 那一种由变量反查选中项，
             * 再叠一条 selected 绑定会两边打架，所以只在静态 options 时生效。 */
            if (b->w->opt_tag < 0) {
                int cnt = (int)lv_dropdown_get_option_cnt(b->w->obj);
                int sel = (int)v;

                if (cnt > 0) {
                    if (sel < 0) sel = 0;
                    if (sel > cnt - 1) sel = cnt - 1;
                    lv_dropdown_set_selected(b->w->obj, (uint32_t)sel);
                }
            }
            break;
        case W_ASCII_DISPLAY: {
            char raw[65];
            int q = TAG_Q_BAD;
            int n = tagdb_str_copy(b->tag_id, raw, (int)sizeof(raw), &q);

            ui_ascii_display_set(b->w, raw, n, q);
            break;
        }
        default:
            break;
        }
    } else if (b->prop == BIND_COLOR) {
        const char* c;

        if (tagdb_quality(b->tag_id) != TAG_Q_GOOD) return;
        c = eval_color(b);
        if (!c) return;
        if (b->w->type == W_LAMP) {
            lv_obj_t* led = widget_paint_obj(b->w);   /* 灯珠是容器的子对象 */

            lv_led_set_color(led, ui_color_hex(c));
            lv_led_set_brightness(led, 255);
        } else {
            lv_obj_set_style_bg_color(b->w->obj, ui_color_hex(c), 0);
        }
    } else if (b->prop == BIND_BG_COLOR) {
        const char* c;

        if (tagdb_quality(b->tag_id) != TAG_Q_GOOD) return;
        c = eval_color(b);
        if (c) lv_obj_set_style_bg_color(widget_paint_obj(b->w), ui_color_hex(c), 0);
    } else if (b->prop == BIND_ENABLED) {
        int on = (tagdb_quality(b->tag_id) == TAG_Q_GOOD) && eval_visible(b);

        if (on) {
            lv_obj_remove_state(b->w->obj, LV_STATE_DISABLED);
            lv_obj_add_flag(b->w->obj, LV_OBJ_FLAG_CLICKABLE);
            lv_obj_clear_flag(b->w->obj, LV_OBJ_FLAG_HIDDEN);
            lv_obj_set_style_opa(b->w->obj, LV_OPA_COVER, 0);
        } else if (b->w->off_hide) {
            /* off_style=hide：整块消失，连"灰按钮"都不占位置 */
            lv_obj_add_state(b->w->obj, LV_STATE_DISABLED);
            lv_obj_clear_flag(b->w->obj, LV_OBJ_FLAG_CLICKABLE);
            lv_obj_add_flag(b->w->obj, LV_OBJ_FLAG_HIDDEN);
        } else {
            lv_obj_add_state(b->w->obj, LV_STATE_DISABLED);
            lv_obj_clear_flag(b->w->obj, LV_OBJ_FLAG_CLICKABLE);
            lv_obj_set_style_opa(b->w->obj, LV_OPA_50, 0);
        }
    } else if (b->prop == BIND_CHECKED) {
        int on = (tagdb_quality(b->tag_id) == TAG_Q_GOOD) && tagdb_bool(b->tag_id);

        if (b->w->type != W_SWITCH && b->w->type != W_CHECKBOX) return;
        if (on) lv_obj_add_state(b->w->obj, LV_STATE_CHECKED);
        else lv_obj_remove_state(b->w->obj, LV_STATE_CHECKED);
        /* 通断文字只有 switch 有；checkbox 的文字是固定标签 */
        if (b->w->type == W_SWITCH && b->w->aux) {
            const char* t = on ? b->w->u.sw.text_on : b->w->u.sw.text_off;

            if (t[0]) lv_label_set_text(b->w->aux, t);
        }
    }
}

void bind_table_refresh(binding_t* items, int count)
{
    for (int i = 0; i < count; i++) {
        uint32_t v;

        if (items[i].kind == BIND_EXPR) {
            /* 表达式可能引用多个变量：把它们的 version 求和当指纹 */
            v = 0;
            for (int k = 0; k < items[i].prog.tag_count; k++)
                v += tagdb_version(items[i].prog.tags[k]);
        } else {
            v = tagdb_version(items[i].tag_id);
        }
        if (v == items[i].last_version) continue;
        apply_binding(&items[i]);
        items[i].last_version = v;
    }
}

/* table 的单元格绑定：props.cells 里每项带 bind 的，生成一条 BIND_TEXT 绑定，
 * 记下行列，应用时直接改那个单元格的 label。 */
static int build_one_cells(const hmi_json_t* wj, widget_t* w, binding_t* e,
                           char* err, size_t errsz)
{
    const hmi_json_t* props = hmi_json_get(wj, "props");
    const hmi_json_t* cells = hmi_json_get(props, "cells");
    const char* wid = hmi_json_str(hmi_json_get(wj, "id"), "?");
    size_t n = hmi_json_count(cells);
    int used = 0;

    if (w->type != W_TABLE) return 0;
    for (size_t i = 0; i < n; i++) {
        const hmi_json_t* cell = hmi_json_at(cells, i);
        const hmi_json_t* b = hmi_json_get(cell, "bind");
        long r = hmi_json_int(hmi_json_get(cell, "r"), -1);
        long c = hmi_json_int(hmi_json_get(cell, "c"), -1);
        long tag;
        const char* kind;

        if (hmi_json_type(b) != HMI_JSON_OBJ) continue;
        if (r < 0 || r > 9 || c < 0 || c > 7) {
            snprintf(err, errsz, "控件 %s: 单元格绑定行列越界（%ld,%ld）", wid, r, c);
            return -1;
        }
        e->w = w;
        e->prop = BIND_TEXT;
        e->kind = BIND_DIRECT;
        e->map_count = 0;
        e->range_count = 0;
        e->bit = 0;
        e->last_version = 0xFFFFFFFFu;
        e->cell_r = (int)r;
        e->cell_c = (int)c;

        tag = hmi_json_int(hmi_json_get(b, "tag"), -1);
        e->tag_id = tagdb_valid(tag) ? (int)tag : -1;
        kind = hmi_json_str(hmi_json_get(b, "kind"), NULL);
        if (kind && strcmp(kind, "fmt") == 0) {
            const char* fmt = hmi_json_str(hmi_json_get(b, "fmt"), NULL);

            if (!fmt || strlen(fmt) >= sizeof(e->fmt)) {
                snprintf(err, errsz, "控件 %s: 单元格绑定的 fmt 非法", wid);
                return -1;
            }
            snprintf(e->fmt, sizeof(e->fmt), "%s", fmt);
            e->kind = BIND_FMT;
        } else if (kind && strcmp(kind, "enum") == 0) {
            const hmi_json_t* map = hmi_json_get(b, "map");
            size_t nm;

            if (hmi_json_type(map) != HMI_JSON_ARR) {
                snprintf(err, errsz, "控件 %s: 单元格绑定 kind=enum 缺 map", wid);
                return -1;
            }
            nm = hmi_json_count(map);
            if (nm == 0 || nm > BIND_MAP_MAX) {
                snprintf(err, errsz, "控件 %s: 单元格 enum 映射项数越界", wid);
                return -1;
            }
            e->kind = BIND_ENUM;
            e->map_count = (int)nm;
            for (size_t k = 0; k < nm; k++) {
                const hmi_json_t* it = hmi_json_at(map, k);
                const char* txt = hmi_json_str(hmi_json_get(it, "text"), NULL);

                if (!txt || !*txt || strlen(txt) >= sizeof(e->map[k].text)) {
                    snprintf(err, errsz, "控件 %s: 单元格 enum 第 %zu 项 text 非法", wid, k);
                    return -1;
                }
                e->map[k].v = hmi_json_num(hmi_json_get(it, "v"), 0);
                snprintf(e->map[k].text, sizeof(e->map[k].text), "%s", txt);
                e->map[k].color[0] = '\0';       /* 单元格只换文字，不换颜色 */
            }
        } else if (kind && strcmp(kind, "bit") != 0 &&
                   strcmp(kind, "expr") != 0) {
            snprintf(err, errsz, "控件 %s: 单元格绑定不支持 kind=%s", wid, kind);
            return -1;
        } else if (kind && strcmp(kind, "expr") == 0) {
            const char* src = hmi_json_str(hmi_json_get(b, "expr"), NULL);

            if (!src || expr_compile(src, &e->prog, err, errsz) != 0) return -1;
            e->kind = BIND_EXPR;
        } else if (kind && strcmp(kind, "bit") == 0) {
            e->kind = BIND_BIT;
            e->bit = (int)hmi_json_int(hmi_json_get(b, "bit"), 0);
        }
        if (e->kind != BIND_EXPR && e->tag_id < 0) {
            snprintf(err, errsz, "控件 %s: 单元格绑定的 tag %ld 不存在", wid, tag);
            return -1;
        }
        e++;
        used++;
    }
    return used;
}
