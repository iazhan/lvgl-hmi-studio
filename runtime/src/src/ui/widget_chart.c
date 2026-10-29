/*
 * widget_chart.c — 柱 / 饼 / 环 图表（37 §6.5 的 chart）
 *
 * 数值来源是 series[i].value 的 {tag|expr}，500ms 重算一次；形状在 DRAW_MAIN 里画
 * （扇形/环扇复用 ui_figure.c 的抗锯齿填充），数值与图例用子标签，刷新时一起挪位置。
 * 契约里 chart 没有轴参数：柱状图只标 y_min / y_max 两个量程值。
 */
#include "widget_chart.h"
#include "ui_figure.h"
#include "ui_font.h"
#include "tagdb.h"
#include "core/i18n.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CHART_MAX_SERIES 8
#define CHART_REFRESH_MS 500
#define CHART_PAD 6
#define CHART_LEG_H 18
#define CHART_GUT_L 34
#define CHART_ARC_SEG_MAX 40
#define CHART_VAL_H 16
#define CHART_VAL_W 56

typedef struct {
    int         tag;
    int         has_expr;
    expr_prog_t expr;
    double      val;
    lv_color_t  color;
    char        label[40];      /* 解析时按当前语言渲染，翻译后可能比原文长 */
} chart_ser_t;

typedef struct {
    int kind;                 /* 0 = bar，1 = pie，2 = donut */
    int n;
    int show_value;
    int show_legend;
    int decimals;
    int ymin, ymax;
    chart_ser_t s[CHART_MAX_SERIES];
    lv_obj_t* vlabel[CHART_MAX_SERIES];   /* 图上的数值 */
    lv_obj_t* lchip[CHART_MAX_SERIES];    /* 图例色块 */
    lv_obj_t* llabel[CHART_MAX_SERIES];   /* 图例文字 */
    lv_obj_t* ylo;
    lv_obj_t* yhi;
    lv_timer_t* timer;
} chart_data_t;

#define CD(w) ((chart_data_t*)(w)->cdata)

static const char* const CHART_PALETTE[] = {
    "#4EA1FF", "#38C172", "#F2B84B", "#E05C5C",
    "#9B6BF2", "#4ECDC4", "#FF9F40", "#9AA7B5"
};

/* 绘图区：柱状图留左列放量程，顶部留一行放图例 */
static void chart_plot(const chart_data_t* d, int ww, int hh,
                       int* px, int* py, int* pw, int* ph)
{
    int top = d->show_legend ? CHART_LEG_H + 2 : CHART_PAD;
    /* 柱状图在绘图区上方留一条放柱顶数值：不留的话，柱子到量程上限时
     * 数值标签会顶进顶部图例里（用户实测到的重叠）。 */
    int band = (d->kind == 0 && d->show_value) ? CHART_VAL_H : 0;

    *px = (d->kind == 0) ? CHART_GUT_L : CHART_PAD;
    *py = top + band;
    *pw = ww - *px - CHART_PAD;
    /* 高度必须从 *py 起算：预留了数值带还按 top 算的话，绘图区会超出控件下沿，
     * 基线和 X 轴就被裁掉（整条 X 轴消失）。 */
    *ph = hh - *py - CHART_PAD;
}

/* 扇形/环扇的顶点：饼是"圆心 + 弧"，环是"外弧 + 内弧反向" */
static int chart_wedge(lv_point_precise_t* pts, int cx, int cy, int ro, int ri,
                       double a0, double a1)
{
    int seg = (int)((a1 - a0) / 6.0 + 0.5);
    int n = 0;

    if (seg < 2) seg = 2;
    if (seg > CHART_ARC_SEG_MAX) seg = CHART_ARC_SEG_MAX;
    if (ri <= 0) {
        pts[n].x = (lv_value_precise_t)cx;
        pts[n].y = (lv_value_precise_t)cy;
        n++;
    }
    for (int i = 0; i <= seg; i++) {
        double a = (a0 + (a1 - a0) * (double)i / (double)seg) *
                   3.14159265358979 / 180.0;

        pts[n].x = (lv_value_precise_t)(cx + lround((double)ro * cos(a)));
        pts[n].y = (lv_value_precise_t)(cy + lround((double)ro * sin(a)));
        n++;
    }
    if (ri > 0) {
        for (int i = seg; i >= 0; i--) {
            double a = (a0 + (a1 - a0) * (double)i / (double)seg) *
                       3.14159265358979 / 180.0;

            pts[n].x = (lv_value_precise_t)(cx + lround((double)ri * cos(a)));
            pts[n].y = (lv_value_precise_t)(cy + lround((double)ri * sin(a)));
            n++;
        }
    }
    return n;
}

static void chart_draw_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    lv_obj_t* obj = lv_event_get_target_obj(e);
    lv_layer_t* layer = lv_event_get_layer(e);
    lv_area_t area;
    chart_data_t* d = CD(w);
    int ww, hh, px, py, pw, ph;

    if (!w || !d || !layer) return;
    lv_obj_get_coords(obj, &area);
    ww = (int)(area.x2 - area.x1 + 1);
    hh = (int)(area.y2 - area.y1 + 1);
    chart_plot(d, ww, hh, &px, &py, &pw, &ph);
    if (pw < 20 || ph < 20 || d->n <= 0) return;

    if (d->kind == 0) {
        double span = (double)d->ymax - (double)d->ymin;
        int slot = pw / d->n;
        int bw = slot * 6 / 10;
        lv_draw_rect_dsc_t rd;

        if (span <= 0) span = 1;
        if (bw < 2) bw = 2;
        lv_draw_rect_dsc_init(&rd);
        rd.border_width = 0;
        rd.radius = 2;
        rd.bg_opa = LV_OPA_COVER;

        /* 基线与左轴：柱状图没有这两条线就看不出量程从哪起 */
        {
            lv_area_t la;
            lv_draw_rect_dsc_t ax;

            lv_draw_rect_dsc_init(&ax);
            ax.bg_color = ui_color_hex("#4A5A6A");
            ax.bg_opa = LV_OPA_COVER;
            ax.border_width = 0;
            la.x1 = (int32_t)(area.x1 + px - 2);
            la.x2 = (int32_t)(area.x1 + px + pw);
            la.y1 = (int32_t)(area.y1 + py + ph);
            la.y2 = la.y1;
            lv_draw_rect(layer, &ax, &la);
            la.x1 = la.x2 = (int32_t)(area.x1 + px - 2);
            la.y1 = (int32_t)(area.y1 + py);
            la.y2 = (int32_t)(area.y1 + py + ph);
            lv_draw_rect(layer, &ax, &la);
        }
        for (int i = 0; i < d->n; i++) {
            double v = d->s[i].val;
            int bx = px + slot * i + (slot - bw) / 2;
            int bh, by;
            lv_area_t ra;

            if (v < (double)d->ymin) v = (double)d->ymin;
            if (v > (double)d->ymax) v = (double)d->ymax;
            bh = (int)((v - (double)d->ymin) / span * (double)ph + 0.5);
            if (bh < 1) bh = 1;
            by = py + ph - bh;
            rd.bg_color = d->s[i].color;
            ra.x1 = (int32_t)(area.x1 + bx);
            ra.y1 = (int32_t)(area.y1 + by);
            ra.x2 = (int32_t)(area.x1 + bx + bw - 1);
            ra.y2 = (int32_t)(area.y1 + by + bh - 1);
            lv_draw_rect(layer, &rd, &ra);
        }
    } else {
        double sum = 0, a = -90.0;          /* 从 12 点方向开始顺时针 */
        int cx = px + pw / 2, cy = py + ph / 2;
        int ro = (pw < ph ? pw : ph) / 2 - 2;
        int ri = (d->kind == 2) ? ro * 55 / 100 : 0;
        lv_point_precise_t pts[2 * (CHART_ARC_SEG_MAX + 1) + 2];

        if (ro < 8) return;
        for (int i = 0; i < d->n; i++)
            if (d->s[i].val > 0) sum += d->s[i].val;
        if (sum <= 0) return;
        for (int i = 0; i < d->n; i++) {
            double v = d->s[i].val > 0 ? d->s[i].val : 0;
            double a1 = a + 360.0 * v / sum;
            int np;

            if (v <= 0) { a = a1; continue; }
            np = chart_wedge(pts, cx, cy, ro, ri, a, a1);
            if (np >= 3)
                fig_fill_poly(layer, (int)area.x1, (int)area.y1, pts, np,
                              d->s[i].color, LV_OPA_COVER);
            a = a1;
        }
    }
}

static int chart_src_eval(widget_t* w, chart_ser_t* s, double* out)
{
    if (s->has_expr) {
        ex_val_t v;

        if (expr_eval(&s->expr, &v) != 0 || v.type == EX_V_STR) return -1;
        *out = v.n;
        return 0;
    }
    (void)w;
    *out = tagdb_num(s->tag);
    return 0;
}

/* 重算数值，并把数值标签/图例摆到新位置 */
static int chart_refresh(widget_t* w)
{
    chart_data_t* d = CD(w);
    lv_obj_t* obj = w->obj;
    lv_area_t area;
    int ww, hh, px, py, pw, ph;
    int changed = 0;
    char buf[32];

    if (!d || !obj) return 0;
    for (int i = 0; i < d->n; i++) {
        double v;

        if (chart_src_eval(w, &d->s[i], &v) != 0) continue;
        if (fabs(v - d->s[i].val) > 1e-9) {
            d->s[i].val = v;
            changed = 1;
        }
    }
    lv_obj_get_coords(obj, &area);
    ww = (int)(area.x2 - area.x1 + 1);
    hh = (int)(area.y2 - area.y1 + 1);
    chart_plot(d, ww, hh, &px, &py, &pw, &ph);

    if (d->kind == 0) {
        if (d->yhi) lv_obj_set_pos(d->yhi, 0, py - 8);
        if (d->ylo) lv_obj_set_pos(d->ylo, 0, py + ph - 8);
    }
    if (d->kind == 0) {
        int slot = pw / (d->n > 0 ? d->n : 1);

        for (int i = 0; i < d->n; i++) {
            double span = (double)d->ymax - (double)d->ymin;
            double v = d->s[i].val;
            int bh, by;

            if (span <= 0) span = 1;
            if (v < (double)d->ymin) v = (double)d->ymin;
            if (v > (double)d->ymax) v = (double)d->ymax;
            bh = (int)((v - (double)d->ymin) / span * (double)ph + 0.5);
            if (bh < 1) bh = 1;
            by = py + ph - bh;
            if (d->vlabel[i]) {
                snprintf(buf, sizeof(buf), "%.*f", d->decimals, d->s[i].val);
                lv_label_set_text(d->vlabel[i], buf);
                lv_obj_set_pos(d->vlabel[i],
                               px + slot * i + slot / 2 - CHART_VAL_W / 2,
                               by - CHART_VAL_H);
            }
        }
    } else {
        double sum = 0, a = -90.0;
        int cx = px + pw / 2, cy = py + ph / 2;
        int ro = (pw < ph ? pw : ph) / 2 - 2;
        int rr = (d->kind == 2) ? ro * 78 / 100 : ro * 65 / 100;

        for (int i = 0; i < d->n; i++)
            if (d->s[i].val > 0) sum += d->s[i].val;
        for (int i = 0; i < d->n; i++) {
            double v = d->s[i].val > 0 ? d->s[i].val : 0;
            double a1 = a + (sum > 0 ? 360.0 * v / sum : 0);
            double am = (a + a1) / 2 * 3.14159265358979 / 180.0;

            if (d->vlabel[i]) {
                snprintf(buf, sizeof(buf), "%.*f", d->decimals, d->s[i].val);
                lv_label_set_text(d->vlabel[i], buf);
                lv_obj_set_pos(d->vlabel[i],
                               cx + (int)((double)rr * cos(am)) - CHART_VAL_W / 2,
                               cy + (int)((double)rr * sin(am)) - CHART_LEG_H / 2);
            }
            a = a1;
        }
    }
    if (changed) lv_obj_invalidate(obj);
    return changed;
}

static void chart_tick_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);

    if (w) (void)chart_refresh(w);
}

void chart_dispose(widget_t* w)
{
    chart_data_t* d;

    /* cdata 是多个控件共用的私有块，必须按类型认领——
     * 之前只判非空，拆报警页时把 alarm_priv_t 当 chart_data_t 解释，
     * 对垃圾指针调 lv_timer_delete/expr_free，然后 free，导致点「返回」闪退。 */
    if (!w || !w->cdata || w->type != W_CHART) return;
    d = CD(w);
    if (d->timer) {
        lv_timer_delete(d->timer);
        d->timer = NULL;
    }
    for (int i = 0; i < CHART_MAX_SERIES; i++)
        if (d->s[i].has_expr) expr_free(&d->s[i].expr);
    free(d);
    w->cdata = NULL;
}

static lv_obj_t* chart_label(lv_obj_t* parent, int fs, const char* color,
                             lv_text_align_t align)
{
    lv_obj_t* l = lv_label_create(parent);

    if (!l) return NULL;
    lv_obj_set_style_text_font(l, ui_font(fs), 0);
    lv_obj_set_style_text_color(l, ui_color_hex(color), 0);
    lv_obj_set_style_text_align(l, align, 0);
    lv_obj_set_style_bg_opa(l, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(l, 0, 0);
    lv_obj_set_style_pad_all(l, 0, 0);
    return l;
}

int chart_build(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out, char* err, size_t errsz)
{
    const char* kind_s = hmi_json_str(hmi_json_get(props, "kind"), "bar");
    const hmi_json_t* series = hmi_json_get(props, "series");
    chart_data_t* d;
    lv_obj_t* box;
    size_t n;

    if (hmi_json_type(series) != HMI_JSON_ARR) {
        snprintf(err, errsz, "chart 缺 series 数组");
        return -1;
    }
    n = hmi_json_count(series);
    if (n < 1 || n > CHART_MAX_SERIES) {
        snprintf(err, errsz, "chart 的 series 需 1..%d 项（给了 %zu）", CHART_MAX_SERIES, n);
        return -1;
    }
    if (ww < 60 || hh < 50) {
        snprintf(err, errsz, "chart 框太小（至少 60x50）");
        return -1;
    }
    d = calloc(1, sizeof(*d));
    if (!d) {
        snprintf(err, errsz, "chart 数据分配失败");
        return -1;
    }
    d->n = (int)n;
    d->kind = (strcmp(kind_s, "pie") == 0) ? 1 : (strcmp(kind_s, "donut") == 0) ? 2 : 0;
    d->show_value = hmi_json_bool(hmi_json_get(props, "show_value"), 1);
    d->show_legend = hmi_json_bool(hmi_json_get(props, "show_legend"), 1);
    d->decimals = (int)hmi_json_int(hmi_json_get(props, "y_decimals"), 0);
    d->ymin = (int)hmi_json_int(hmi_json_get(props, "y_min"), 0);
    d->ymax = (int)hmi_json_int(hmi_json_get(props, "y_max"), 100);
    if (d->decimals < 0) d->decimals = 0;
    if (d->decimals > 6) d->decimals = 6;
    if (d->ymax <= d->ymin) {
        snprintf(err, errsz, "chart 量程非法（y %d..%d）", d->ymin, d->ymax);
        free(d);
        return -1;
    }

    box = lv_obj_create(ui_cur_parent());
    if (!box) {
        free(d);
        return -1;
    }
    ui_place(box, x, y, ww, hh);
    lv_obj_remove_flag(box, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_opa(box, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(box, 0, 0);
    out->obj = box;
    out->type = W_CHART;
    out->cdata = d;
    lv_obj_add_event_cb(box, chart_draw_cb, LV_EVENT_DRAW_MAIN, out);

    for (int i = 0; i < d->n; i++) {
        const hmi_json_t* c = hmi_json_at(series, (size_t)i);
        const hmi_json_t* val = hmi_json_get(c, "value");
        const char* color = hmi_json_str(hmi_json_get(c, "color"), CHART_PALETTE[i]);
        const char* label = hmi_json_str(hmi_json_get(c, "label"), NULL);
        long tag = hmi_json_int(hmi_json_get(val, "tag"), -1);
        const char* ex = hmi_json_str(hmi_json_get(val, "expr"), NULL);

        if (!ui_color_valid(color)) {
            snprintf(err, errsz, "chart 第 %d 项的 color 非法", i);
            chart_dispose(out);
            return -1;
        }
        d->s[i].color = ui_color_hex(color);
        /* 图例文字可以写 @id：解析时渲染一次，图例与刷新都不用再查表 */
        i18n_text(label, d->s[i].label, sizeof(d->s[i].label));
        if (ex && *ex) {
            char eerr[160];

            if (tag >= 0) {
                snprintf(err, errsz, "chart 第 %d 项同时给了 tag 与 expr，二选一", i);
                chart_dispose(out);
                return -1;
            }
            if (expr_compile(ex, &d->s[i].expr, eerr, sizeof(eerr)) != 0) {
                snprintf(err, errsz, "chart 第 %d 项的 expr 编译失败：%s", i, eerr);
                chart_dispose(out);
                return -1;
            }
            d->s[i].has_expr = 1;
            d->s[i].tag = -1;
        } else {
            if (!tagdb_valid(tag)) {
                snprintf(err, errsz, "chart 第 %d 项既没有 expr，tag %ld 也不存在", i, tag);
                chart_dispose(out);
                return -1;
            }
            d->s[i].tag = (int)tag;
        }
        d->s[i].val = 0;
    }

    /* 图例：色块 + 文字（顶部一行） */
    if (d->show_legend) {
        int step = (int)ww / d->n;

        for (int i = 0; i < d->n; i++) {
            lv_obj_t* chip = lv_obj_create(box);
            lv_obj_t* lb;

            if (chip) {
                lv_obj_set_pos(chip, 4 + step * i, 4);
                lv_obj_set_size(chip, 8, 8);
                lv_obj_set_style_bg_color(chip, d->s[i].color, 0);
                lv_obj_set_style_bg_opa(chip, LV_OPA_COVER, 0);
                lv_obj_set_style_border_width(chip, 0, 0);
                lv_obj_set_style_radius(chip, 1, 0);
                lv_obj_set_style_pad_all(chip, 0, 0);
                lv_obj_remove_flag(chip, LV_OBJ_FLAG_SCROLLABLE);
                d->lchip[i] = chip;
            }
            lb = chart_label(box, 12, "#C8D2DC", LV_TEXT_ALIGN_LEFT);
            if (lb) {
                lv_label_set_text(lb, d->s[i].label);
                lv_obj_set_pos(lb, 4 + step * i + 12, 1);
                lv_obj_set_size(lb, step - 14 > 20 ? step - 14 : 20, CHART_LEG_H);
                d->llabel[i] = lb;
            }
        }
    }

    /* 柱状图的两个量程标 */
    if (d->kind == 0) {
        char buf[24];

        d->yhi = chart_label(box, 12, "#C8D2DC", LV_TEXT_ALIGN_RIGHT);
        d->ylo = chart_label(box, 12, "#C8D2DC", LV_TEXT_ALIGN_RIGHT);
        if (d->yhi) {
            snprintf(buf, sizeof(buf), "%d", d->ymax);
            lv_label_set_text(d->yhi, buf);
            lv_obj_set_size(d->yhi, CHART_GUT_L - 6, 16);
        }
        if (d->ylo) {
            snprintf(buf, sizeof(buf), "%d", d->ymin);
            lv_label_set_text(d->ylo, buf);
            lv_obj_set_size(d->ylo, CHART_GUT_L - 6, 16);
        }
    }

    /* 图上的数值 */
    if (d->show_value) {
        for (int i = 0; i < d->n; i++) {
            lv_obj_t* lb = chart_label(box, 12, "#E6EDF3", LV_TEXT_ALIGN_CENTER);

            if (!lb) continue;
            lv_obj_set_size(lb, CHART_VAL_W, CHART_LEG_H);
            d->vlabel[i] = lb;
        }
    }

    (void)chart_refresh(out);
    d->timer = lv_timer_create(chart_tick_cb, CHART_REFRESH_MS, out);
    if (!d->timer) {
        snprintf(err, errsz, "chart 刷新定时器创建失败");
        chart_dispose(out);
        return -1;
    }
    return 0;
}
