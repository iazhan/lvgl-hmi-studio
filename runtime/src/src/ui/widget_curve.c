/*
 * widget_curve.c — 三条曲线：realtime / history / xy（共用采样、图例、拖动浏览）
 */
#include "widgets_internal.h"
#include "ui_font.h"
#include "core/expr.h"
#include "core/i18n.h"
#include "data/savecfg.h"
#include "data/trend_logger.h"
#include "data/trend_store.h"
#include "hmi_log.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static void axis_line(lv_obj_t* parent, int x, int y, int w, int h, const char* color)
{
    lv_obj_t* o = lv_obj_create(parent);

    if (!o) return;
    lv_obj_set_pos(o, (int32_t)x, (int32_t)y);
    lv_obj_set_size(o, (int32_t)w, (int32_t)h);
    lv_obj_set_style_bg_color(o, ui_color_hex(color), 0);
    lv_obj_set_style_bg_opa(o, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(o, 0, 0);
    lv_obj_set_style_radius(o, 0, 0);
    lv_obj_set_style_pad_all(o, 0, 0);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
}

/* 轴标签：右对齐（Y 轴）或居中（X 轴） */
static void axis_label(lv_obj_t* parent, int x, int y, int w, int h, const char* text,
                       int fs, const char* color, lv_text_align_t align)
{
    lv_obj_t* lb = lv_label_create(parent);

    if (!lb) return;
    lv_label_set_text(lb, text);
    lv_obj_set_pos(lb, (int32_t)x, (int32_t)y);
    lv_obj_set_size(lb, (int32_t)w, (int32_t)h);
    lv_obj_set_style_text_font(lb, ui_font(fs), 0);
    lv_obj_set_style_text_color(lb, ui_color_hex(color), 0);
    lv_obj_set_style_text_align(lb, align, 0);
    lv_obj_set_style_bg_opa(lb, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(lb, 0, 0);
    lv_obj_set_style_pad_all(lb, 0, 0);
}

/* 趋势数据根目录由 widgets_common.c 记着（history_table 也要用），见 ui_trend_dir() */

/* ── 曲线（37 §6.6）：realtime_curve 与 history_curve 共用一套 ──
 * 两者只有数据来源不同：
 *   realtime：自己的定时器采样进环形缓冲
 *   history ：进页面时从趋势文件读进同样的环形缓冲，再按 refresh_ms 重读
 * 轴、网格、刻度、标签、双 Y 轴、拖动回看都在这一份里，避免两套实现各自缺东西。
 *
 * 数据进 chart 时会按 bucket 做桶平均：历史 15 分钟 = 900 个 1 秒采样，但图只有 150 个
 * 点，一个点要覆盖 6 个采样，否则不是画不下而是 "窗口" 和 "点数" 概念混在一起。 */
static void curve_update_labels(widget_t* w)
{
    char buf[24];
    int div = w->u.curve.xlabel_total_div ? w->u.curve.xlabel_total_div : 1;
    long span = w->u.curve.xlabel_span;
    long off_s = (long)w->u.curve.view_offset * w->u.curve.sample_ms / 1000;
    long now_ref = w->u.curve.t_ref ? w->u.curve.t_ref : (long)time(NULL);
    int dec = (span < 3 * div) ? 1 : 0;

    if (w->u.curve.nxlabels <= 0) return;

    for (int i = 0; i < w->u.curve.nxlabels; i++) {
        long d = div - w->u.curve.xlabel_k[i];          /* 0 = 最左，div = 最右 */

        if (w->u.curve.x_clock) {
            /* 窗口右端 = 最新数据时刻 - 回看偏移；左端 = 右端 - 窗口宽度。
             * 这样轴起点随时间前进（滑动窗口），不会把已有数据一路压缩。 */
            time_t tt = (time_t)(now_ref - off_s - span + span * d / div);
            struct tm tmv;

            localtime_r(&tt, &tmv);
            if (strcmp(w->u.curve.x_fmt, "clock_short") == 0)
                strftime(buf, sizeof(buf), "%M:%S", &tmv);
            else if (w->u.curve.span_min > 60)
                strftime(buf, sizeof(buf), "%H:%M", &tmv);
            else
                strftime(buf, sizeof(buf), "%H:%M:%S", &tmv);
        } else if (w->u.curve.x_fmt[0] == 'e') {
            /* elapsed：0 = 第一条记录。数据不足一窗时轴就是 0..span */
            long base_slots = w->u.curve.hist_n - (long)w->u.curve.bucket *
                              (long)lv_chart_get_point_count(w->u.curve.chart) - w->u.curve.view_offset;

            if (base_slots < 0) base_slots = 0;
            if (w->u.curve.hist_n <= (long)w->u.curve.bucket * (long)lv_chart_get_point_count(w->u.curve.chart))
                base_slots = 0;
            snprintf(buf, sizeof(buf), "%.*fs", dec,
                     (double)(base_slots * w->u.curve.sample_ms / 1000 + span * d / div));
        } else {
            snprintf(buf, sizeof(buf), "-%.*fs", dec,
                     (double)(span * (div - d) / div + off_s));
        }
        lv_label_set_text(w->u.curve.xlabels[i], buf);
    }
}

/* 把环形缓冲里当前视窗按 bucket 平均后填进 chart */
static void curve_refill(widget_t* w)
{
    int32_t buf[300];
    int pts = (int)lv_chart_get_point_count(w->u.curve.chart);
    int span, start, first, bucket;

    if (!w->u.curve.hist_cap || w->u.curve.hist_n <= 0) return;
    if (pts > 300) pts = 300;
    bucket = w->u.curve.bucket > 0 ? w->u.curve.bucket : 1;
    span = pts * bucket;
    /* 数据不足一窗时：
     *   rel / history → 贴右端（最新在右，左边空着）
     *   elapsed       → 贴左端（0 在左，右边留空） */
    if (w->u.curve.x_left_grow && w->u.curve.hist_n <= span) start = 0;
    else start = w->u.curve.hist_n - span - w->u.curve.view_offset;
    first = (w->u.curve.hist_head - w->u.curve.hist_n + w->u.curve.hist_cap) % w->u.curve.hist_cap;

    for (int i = 0; i < w->u.curve.nseries; i++) {
        for (int k = 0; k < pts; k++) {
            int b0 = start + k * bucket;
            int b1 = b0 + bucket;
            double sum = 0;
            int cnt = 0;

            if (b1 <= 0) { buf[k] = LV_CHART_POINT_NONE; continue; }
            if (b0 < 0) b0 = 0;
            if (b1 > w->u.curve.hist_n) b1 = w->u.curve.hist_n;
            for (int j = b0; j < b1; j++) {
                sum += w->u.curve.hist[i][(first + j) % w->u.curve.hist_cap];
                cnt++;
            }
            buf[k] = cnt ? (int32_t)(sum / cnt) : LV_CHART_POINT_NONE;
        }
        lv_chart_set_series_values(w->u.curve.chart, w->u.curve.series[i], buf, (size_t)pts);
    }
    lv_chart_refresh(w->u.curve.chart);
}

static void curve_browse_tag_update(widget_t* w)
{
    char buf[32];

    if (!w->u.curve.browse_tag) return;
    if (w->u.curve.view_offset <= 0) {
        lv_obj_add_flag(w->u.curve.browse_tag, LV_OBJ_FLAG_HIDDEN);
        return;
    }
    lv_obj_remove_flag(w->u.curve.browse_tag, LV_OBJ_FLAG_HIDDEN);
    snprintf(buf, sizeof(buf), "浏览 -%lds",
             (long)w->u.curve.view_offset * w->u.curve.sample_ms / 1000);
    lv_label_set_text(w->u.curve.browse_tag, buf);
}

/* 实时采样 */
static void curve_sample_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);

    if (!w || !w->u.curve.chart) return;
    for (int i = 0; i < w->u.curve.nseries; i++) {
        double v;
        int32_t iv;

        if (w->u.curve.curve_has_expr[i]) {
            ex_val_t val;

            if (expr_eval(&w->u.curve.curve_expr[i], &val) != 0) v = (double)w->u.curve.last_v[i];
            else v = (val.type == EX_V_STR && val.s) ? strtod(val.s, NULL) : val.n;
        } else {
            v = tagdb_num(w->u.curve.curve_tags[i]);
        }
        iv = (int32_t)v;
        w->u.curve.last_v[i] = iv;
        if (w->u.curve.hist_cap > 0 && w->u.curve.hist[i]) w->u.curve.hist[i][w->u.curve.hist_head] = iv;
    }
    if (w->u.curve.hist_cap > 0) {
        w->u.curve.hist_head = (w->u.curve.hist_head + 1) % w->u.curve.hist_cap;
        if (w->u.curve.hist_n < w->u.curve.hist_cap) w->u.curve.hist_n++;
    }
    w->u.curve.t_ref = (long)time(NULL);

    if (w->u.curve.view_offset == 0) {
        if (w->u.curve.bucket > 1 || w->u.curve.x_left_grow) curve_refill(w);
        else {
            for (int i = 0; i < w->u.curve.nseries; i++)
                lv_chart_set_next_value(w->u.curve.chart, w->u.curve.series[i], w->u.curve.last_v[i]);
            lv_chart_refresh(w->u.curve.chart);
        }
    }
    curve_update_labels(w);
}

static void curve_press_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    lv_point_t p;

    if (!w) return;
    lv_indev_get_point(lv_indev_active(), &p);
    w->u.curve.press_x0 = p.x;
    w->u.curve.press_offset = w->u.curve.view_offset;
    w->u.curve.dragged = 0;
}

static void curve_pressing_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    lv_point_t p;
    int dx, dpts, off, max_off, pts, bucket;

    if (!w || w->u.curve.chart_w <= 0 || !w->u.curve.hist_cap) return;
    lv_indev_get_point(lv_indev_active(), &p);
    pts = (int)lv_chart_get_point_count(w->u.curve.chart);
    bucket = w->u.curve.bucket > 0 ? w->u.curve.bucket : 1;
    dx = p.x - w->u.curve.press_x0;
    /* 直接操纵：手指往右拖，内容跟着往右走（左边露出更早的数据） */
    dpts = (dx * pts / w->u.curve.chart_w) * bucket;
    max_off = w->u.curve.hist_n - pts * bucket;
    if (max_off < 0) max_off = 0;
    off = w->u.curve.press_offset + dpts;
    if (off < 0) off = 0;
    if (off > max_off) off = max_off;
    if (off == w->u.curve.view_offset) return;
    w->u.curve.view_offset = off;
    w->u.curve.dragged = 1;
    curve_refill(w);
    curve_browse_tag_update(w);
    curve_update_labels(w);
}

static void curve_click_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);

    if (!w || w->u.curve.view_offset == 0) return;
    if (w->u.curve.dragged) {
        w->u.curve.dragged = 0;
        return;
    }
    w->u.curve.view_offset = 0;
    curve_refill(w);
    curve_browse_tag_update(w);
    curve_update_labels(w);
}

/* 历史曲线：从趋势文件重新读进环形缓冲 */
static int curve_history_reload(widget_t* w)
{
    trend_rec_t* recs = NULL;
    int nch = 0, iv = 0, n;

    n = trend_read_all(ui_trend_dir(), w->u.curve.tgroup, &recs, &nch, &iv);
    if (n <= 0) {
        free(recs);
        if (w->u.curve.tinfo) lv_obj_remove_flag(w->u.curve.tinfo, LV_OBJ_FLAG_HIDDEN);
        return -1;
    }
    if (w->u.curve.tinfo) lv_obj_add_flag(w->u.curve.tinfo, LV_OBJ_FLAG_HIDDEN);

    if (w->u.curve.hist_cap != n) {
        for (int i = 0; i < 4; i++) {
            free(w->u.curve.hist[i]);
            w->u.curve.hist[i] = calloc((size_t)n, sizeof(int32_t));
            if (!w->u.curve.hist[i]) {
                HMI_LOG("history: 缓冲分配失败（%d 条）", n);
                free(recs);
                return -1;
            }
        }
        w->u.curve.hist_cap = n;
    }
    for (int i = 0; i < w->u.curve.nseries; i++) {
        int ch = w->u.curve.tchidx[i];

        if (ch < 0 || ch >= nch) continue;
        for (int k = 0; k < n; k++)
            w->u.curve.hist[i][k] = (int32_t)recs[k].v[ch];
    }
    w->u.curve.hist_n = n;
    w->u.curve.hist_head = n % (n ? n : 1);
    w->u.curve.sample_ms = iv > 0 ? iv : 1000;
    w->u.curve.t_ref = (long)recs[n - 1].ts;
    if (w->u.curve.view_offset > 0) {
        int pts = (int)lv_chart_get_point_count(w->u.curve.chart);
        int max_off = n - pts * (w->u.curve.bucket > 0 ? w->u.curve.bucket : 1);

        if (max_off < 0) max_off = 0;
        if (w->u.curve.view_offset > max_off) w->u.curve.view_offset = max_off;
    }
    free(recs);
    curve_refill(w);
    curve_update_labels(w);
    return 0;
}

static void history_timer_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);

    if (w && w->u.curve.chart) (void)curve_history_reload(w);
}

int build_curve(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out, char* err, size_t errsz, int is_history)
{
    const hmi_json_t* curves = hmi_json_get(props, "curves");
    long sample_ms = hmi_json_int(hmi_json_get(props, "sample_ms"), 1000);
    /* 窗口用「秒」表达（37 §6.6，契约 v1）：图点数由运行时算，不让用户填 */
    long span_s = hmi_json_int(hmi_json_get(props, "span_s"), is_history ? 900 : 60);
    long history_s = hmi_json_int(hmi_json_get(props, "history_s"), 0);
    long points = is_history ? 150 : 60;   /* 历史曲线下面按实际采样重算 */
    long ymin = hmi_json_int(hmi_json_get(props, "y_min"), 0);
    long ymax = hmi_json_int(hmi_json_get(props, "y_max"), 100);
    long y2min = hmi_json_int(hmi_json_get(props, "y2_min"), 0);
    long y2max = hmi_json_int(hmi_json_get(props, "y2_max"), 100);
    int grid = hmi_json_bool(hmi_json_get(props, "grid"), 1);
    int legend = hmi_json_bool(hmi_json_get(props, "legend"), 1);
    int y_dec = (int)hmi_json_int(hmi_json_get(props, "y_decimals"), 0);
    int y_digits = (int)hmi_json_int(hmi_json_get(props, "y_digits"), 0);
    int y2_dec = (int)hmi_json_int(hmi_json_get(props, "y2_decimals"), y_dec);
    int y2_digits = (int)hmi_json_int(hmi_json_get(props, "y2_digits"), y_digits);
    int y_div = (int)hmi_json_int(hmi_json_get(props, "y_div"), 4);
    int y_minor = (int)hmi_json_int(hmi_json_get(props, "y_minor"), 0);
    int x_div = (int)hmi_json_int(hmi_json_get(props, "x_div"), 4);
    int x_minor = (int)hmi_json_int(hmi_json_get(props, "x_minor"), 0);
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 14);
    const char* xf = hmi_json_str(hmi_json_get(props, "x_format"), is_history ? "clock" : "rel");
    long x_step = hmi_json_int(hmi_json_get(props, "x_label_step"), 0);
    const char* axis_color = hmi_json_str(hmi_json_get(props, "axis_color"), "#4A5A6A");
    const char* grid_color = hmi_json_str(hmi_json_get(props, "grid_color"), "#2F3D4C");
    const char* tick_color = hmi_json_str(hmi_json_get(props, "tick_color"), "#9AA7B5");
    const char* label_color = hmi_json_str(hmi_json_get(props, "label_color"), "#C8D2DC");
    const char* bg_color = hmi_json_str(hmi_json_get(props, "bg_color"), "#141C26");
    const char* border_color = hmi_json_str(hmi_json_get(props, "border_color"), "#3A4A5A");
    int show_axis = hmi_json_bool(hmi_json_get(props, "show_axis"), 1);
    int show_ticks = hmi_json_bool(hmi_json_get(props, "show_ticks"), 1);
    int show_labels = hmi_json_bool(hmi_json_get(props, "show_labels"), 1);
    const char* dir = hmi_json_str(hmi_json_get(props, "direction"), "lr");
    const hmi_json_t* range = hmi_json_get(props, "range");
    long span_min = hmi_json_int(hmi_json_get(range, "last_min"), 30);   /* 只支持 last_min */
    long refresh = hmi_json_int(hmi_json_get(props, "refresh_ms"), 5000);
    const int* gtags = NULL;
    int gch = 0;
    size_t n;
    lv_obj_t* o;
    int gutter_l, gutter_b, gutter_r, plot_w, plot_h;

    if (hmi_json_type(curves) != HMI_JSON_ARR) {
        snprintf(err, errsz, "%s 缺 props.curves 数组", is_history ? "history_curve" : "realtime_curve");
        return -1;
    }
    n = hmi_json_count(curves);
    if (n == 0 || n > 4) {
        snprintf(err, errsz, "曲线的 curves 项数 %zu 超出 1..4", n);
        return -1;
    }
    if (span_s < 1 || span_s > 3600) {
        snprintf(err, errsz, "曲线的 span_s %ld 越界（1..3600 秒）", span_s);
        return -1;
    }
    /* 图点数 = 窗口宽度 / 采样周期，钳到 2..300（37 §6.6） */
    if (!is_history) {
        points = span_s * 1000 / (sample_ms > 0 ? sample_ms : 1000);
        if (points < 2) points = 2;
        if (points > 300) points = 300;
    }
    if (points < 2 || points > 300) {
        snprintf(err, errsz, "曲线的 points %ld 越界（2..300）", points);
        return -1;
    }
    if (ymax <= ymin) {
        snprintf(err, errsz, "曲线的 y_max 必须大于 y_min");
        return -1;
    }
    if (y_div < 1 || y_div > 10 || y_minor < 0 || y_minor > 5 ||
        x_div < 1 || x_div > 10 || x_minor < 0 || x_minor > 5) {
        snprintf(err, errsz, "曲线的 y_div/x_div（1..10）或 y_minor/x_minor（0..5）越界");
        return -1;
    }
    if (fs < 10 || fs > 32) {
        snprintf(err, errsz, "曲线的 font_size %d 越界（10..32）", fs);
        return -1;
    }
    if (!ui_color_valid(axis_color) || !ui_color_valid(grid_color) ||
        !ui_color_valid(tick_color) || !ui_color_valid(label_color) ||
        !ui_color_valid(bg_color) || !ui_color_valid(border_color)) {
        snprintf(err, errsz, "曲线的颜色属性必须是 #RRGGBB");
        return -1;
    }
    if (strcmp(dir, "lr") != 0) {
        snprintf(err, errsz, "曲线的 direction=%s 暂未实现（v1 只有 lr）", dir);
        return -1;
    }
    if (strcmp(xf, "rel") != 0 && strcmp(xf, "elapsed") != 0 && strcmp(xf, "clock") != 0 &&
        strcmp(xf, "clock_short") != 0) {
        snprintf(err, errsz, "曲线的 x_format=%s 非法（rel/elapsed/clock/clock_short）", xf);
        return -1;
    }
    if (is_history) {
        const char* group = hmi_json_str(hmi_json_get(props, "group"), NULL);

        if (!group || !*group) {
            snprintf(err, errsz, "history_curve 缺 props.group");
            return -1;
        }
        if (trend_logger_group(group, &gch, &gtags) != 0 &&
        savecfg_group(group, &gch, &gtags) != 0) {
            snprintf(err, errsz, "history_curve 的 group=%s 未在 trends.json 里声明", group);
            return -1;
        }
        if (span_min < 1 || span_min > 1440) {
            snprintf(err, errsz, "history_curve 的 span_min %ld 越界（1..1440）", span_min);
            return -1;
        }
        snprintf(out->u.curve.tgroup, sizeof(out->u.curve.tgroup), "%s", group);
    } else {
        if (sample_ms < 200 || sample_ms > 60000) {
            snprintf(err, errsz, "realtime_curve 的 sample_ms %ld 越界（200..60000）", sample_ms);
            return -1;
        }
        out->u.curve.sample_ms = sample_ms;
    }

    /* 右轴预扫 */
    {
        int has_right = 0;

        for (size_t i = 0; i < n; i++) {
            const char* ax = hmi_json_str(hmi_json_get(hmi_json_at(curves, i), "axis"), "left");

            if (strcmp(ax, "right") == 0) has_right = 1;
            else if (strcmp(ax, "left") != 0) {
                snprintf(err, errsz, "曲线第 %zu 条的 axis=%s 非法（left/right）", i, ax);
                return -1;
            }
        }
        if (has_right && y2max == y2min) {
            snprintf(err, errsz, "曲线用了右轴，但 y2_max 与 y2_min 相等");
            return -1;
        }
        out->u.curve.has_right_axis = has_right;
    }

    if (!is_history) {
        /* 缓冲点数 = 可回看时长 / 采样周期；至少覆盖一窗 */
        long hcap = (history_s > 0) ? history_s * 1000 / sample_ms : points;

        if (hcap < points) hcap = points;
        if (hcap > 1200) hcap = 1200;
        out->u.curve.hist_cap = (int)hcap;
        for (size_t i = 0; i < n; i++) {
            out->u.curve.hist[i] = calloc((size_t)hcap, sizeof(int32_t));
            if (!out->u.curve.hist[i]) {
                snprintf(err, errsz, "曲线历史缓冲分配失败（%ld 点）", hcap);
                return -1;
            }
        }
    }

    if (ww < 120 || hh < 80) {
        snprintf(err, errsz, "曲线的 w/h 太小（至少 120x80，要给坐标轴留位置）");
        return -1;
    }

    gutter_l = show_labels ? (fs >= 14 ? 40 : 34) : 6;
    gutter_b = show_labels ? 24 : 6;
    gutter_r = (out->u.curve.has_right_axis && show_labels) ? (fs >= 14 ? 40 : 34) : 0;
    plot_w = (int)ww - gutter_l - gutter_r - 4;
    plot_h = (int)hh - gutter_b - 2;
    if (plot_w < 60) {
        snprintf(err, errsz, "曲线太窄：双轴时左右留白后绘图区只剩 %d px", plot_w);
        return -1;
    }
    out->u.curve.chart_w = plot_w;

    {
        lv_obj_t* root = lv_obj_create(cur_parent());

        if (!root) {
            snprintf(err, errsz, "曲线建控件失败");
            return -1;
        }
        place(root, x, y, ww, hh);
        lv_obj_set_style_bg_opa(root, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(root, 0, 0);
        lv_obj_set_style_pad_all(root, 0, 0);
        lv_obj_remove_flag(root, LV_OBJ_FLAG_SCROLLABLE);
        out->obj = root;
    }
    o = lv_chart_create(out->obj);
    if (!o) {
        snprintf(err, errsz, "曲线建 chart 失败");
        return -1;
    }
    lv_obj_set_pos(o, (int32_t)gutter_l, 2);
    lv_obj_set_size(o, (int32_t)plot_w, (int32_t)plot_h);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);   /* 否则 PRESSING 手势被它吞掉 */
    lv_chart_set_type(o, LV_CHART_TYPE_LINE);
    lv_chart_set_point_count(o, (uint32_t)points);
    lv_chart_set_range(o, LV_CHART_AXIS_PRIMARY_Y, (int32_t)ymin, (int32_t)ymax);
    if (out->u.curve.has_right_axis)
        lv_chart_set_range(o, LV_CHART_AXIS_SECONDARY_Y, (int32_t)y2min, (int32_t)y2max);
    lv_chart_set_div_line_count(o, 0, 0);
    lv_obj_set_style_bg_color(o, ui_color_hex(bg_color), 0);
    lv_obj_set_style_border_color(o, ui_color_hex(border_color), 0);
    lv_obj_set_style_border_width(o, 1, 0);
    lv_obj_set_style_radius(o, 2, 0);
    lv_obj_set_style_pad_all(o, 0, 0);
    lv_obj_set_style_line_width(o, 2, LV_PART_ITEMS);
    lv_obj_set_style_size(o, 0, 0, LV_PART_INDICATOR);
    out->u.curve.chart = o;
    out->type = is_history ? W_HISTORY_CURVE : W_REALTIME_CURVE;
    lv_obj_add_event_cb(o, curve_press_cb, LV_EVENT_PRESSED, out);
    lv_obj_add_event_cb(o, curve_pressing_cb, LV_EVENT_PRESSING, out);
    lv_obj_add_event_cb(o, curve_click_cb, LV_EVENT_CLICKED, out);

    for (size_t i = 0; i < n; i++) {
        const hmi_json_t* c = hmi_json_at(curves, i);
        long tag = hmi_json_int(hmi_json_get(c, "tag"), -1);
        const char* ex = hmi_json_str(hmi_json_get(c, "expr"), NULL);
        const char* color = hmi_json_str(hmi_json_get(c, "color"), "#4EA1FF");
        const char* label = hmi_json_str(hmi_json_get(c, "label"), NULL);
        const char* ax = hmi_json_str(hmi_json_get(c, "axis"), "left");
        lv_chart_axis_t axis = (strcmp(ax, "right") == 0) ? LV_CHART_AXIS_SECONDARY_Y
                                                          : LV_CHART_AXIS_PRIMARY_Y;

        if (!ui_color_valid(color)) {
            snprintf(err, errsz, "曲线第 %zu 条的 color 非法", i);
            return -1;
        }
        if (is_history) {
            int ch = -1;

            for (int k = 0; k < gch; k++)
                if (gtags[k] == (int)tag) { ch = k; break; }
            if (ch < 0) {
                snprintf(err, errsz, "history_curve 第 %zu 条的 tag %ld 不在趋势组 %s 里",
                         i, tag, out->u.curve.tgroup);
                return -1;
            }
            out->u.curve.tchidx[i] = ch;
            out->u.curve.curve_tags[i] = (int)tag;
        } else if (ex && *ex) {
            char eerr[160];

            if (tag >= 0) {
                snprintf(err, errsz, "曲线第 %zu 条同时给了 tag 与 expr，二选一", i);
                return -1;
            }
            if (expr_compile(ex, &out->u.curve.curve_expr[i], eerr, sizeof(eerr)) != 0) {
                snprintf(err, errsz, "曲线第 %zu 条的 expr 编译失败：%s", i, eerr);
                return -1;
            }
            out->u.curve.curve_has_expr[i] = 1;
            out->u.curve.curve_tags[i] = -1;
        } else {
            if (!tagdb_valid(tag)) {
                snprintf(err, errsz, "曲线第 %zu 条既没有 expr，tag %ld 也不存在", i, tag);
                return -1;
            }
            out->u.curve.curve_tags[i] = (int)tag;
        }

        out->u.curve.series[i] = lv_chart_add_series(o, ui_color_hex(color), axis);
        if (!out->u.curve.series[i]) {
            snprintf(err, errsz, "曲线加系列失败");
            return -1;
        }
        out->u.curve.nseries = (int)i + 1;

        if (legend && label && *label) {
            lv_obj_t* lb = lv_label_create(out->obj);
            char tb[96];

            lv_label_set_text(lb, i18n_text(label, tb, sizeof(tb)));
            lv_obj_set_style_text_font(lb, ui_font(fs >= 14 ? 14 : 12), 0);
            lv_obj_set_style_text_color(lb, ui_color_hex(color), 0);
            lv_obj_set_style_bg_opa(lb, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(lb, 0, 0);
            lv_obj_set_pos(lb, (int32_t)(gutter_l + 4 + (int)i * 68), 4);
        }
    }

    /* 网格：主分隔 + 次分隔 */
    if (grid) {
        for (int k = 0; k <= y_div; k++) {
            axis_line(out->obj, gutter_l, 2 + plot_h * k / y_div, plot_w, 1, grid_color);
            for (int m = 1; m <= y_minor && k < y_div; m++)
                axis_line(out->obj, gutter_l,
                          2 + plot_h * k / y_div + plot_h * m / (y_div * (y_minor + 1)),
                          plot_w, 1, "#22303C");
        }
        for (int k = 0; k <= x_div; k++) {
            axis_line(out->obj, gutter_l + plot_w * k / x_div, 2, 1, plot_h, grid_color);
            for (int m = 1; m <= x_minor && k < x_div; m++)
                axis_line(out->obj, gutter_l + plot_w * k / x_div +
                          plot_w * m / (x_div * (x_minor + 1)), 2, 1, plot_h, "#22303C");
        }
    }

    /* 轴线 + 刻度 */
    if (show_axis) {
        axis_line(out->obj, gutter_l - 2, 2, 1, plot_h, axis_color);
        axis_line(out->obj, gutter_l - 2, 2 + plot_h, plot_w + 2, 1, axis_color);
        if (out->u.curve.has_right_axis)
            axis_line(out->obj, gutter_l + plot_w + 2, 2, 1, plot_h, axis_color);
    }
    if (show_ticks) {
        for (int k = 0; k <= y_div; k++) {
            axis_line(out->obj, gutter_l - 6, 2 + plot_h * k / y_div, 5, 1, tick_color);
            if (out->u.curve.has_right_axis)
                axis_line(out->obj, gutter_l + plot_w + 2, 2 + plot_h * k / y_div, 5, 1, tick_color);
        }
        for (int k = 0; k <= x_div; k++)
            axis_line(out->obj, gutter_l + plot_w * k / x_div, 3 + plot_h, 1, 4, tick_color);
    }

    /* Y 标签（左右两列） */
    if (show_labels) {
        char buf[24];

        for (int k = 0; k <= y_div; k++) {
            long v = ymax - (long)((double)(ymax - ymin) * k / y_div);
            int yy = 2 + plot_h * k / y_div - fs / 2;

            if (yy < 0) yy = 0;
            if (yy > 2 + plot_h - fs) yy = 2 + plot_h - fs;
            if (y_digits > 0) snprintf(buf, sizeof(buf), "%*.*f", y_digits, y_dec, (double)v);
            else snprintf(buf, sizeof(buf), "%.*f", y_dec, (double)v);
            axis_label(out->obj, 0, yy, gutter_l - 8, (int)fs + 2, buf, fs, label_color,
                       LV_TEXT_ALIGN_RIGHT);
        }
        if (out->u.curve.has_right_axis) {
            for (int k = 0; k <= y_div; k++) {
                long v = y2max - (long)((double)(y2max - y2min) * k / y_div);
                int yy = 2 + plot_h * k / y_div - fs / 2;

                if (yy < 0) yy = 0;
                if (yy > 2 + plot_h - fs) yy = 2 + plot_h - fs;
                if (y2_digits > 0) snprintf(buf, sizeof(buf), "%*.*f", y2_digits, y2_dec, (double)v);
                else snprintf(buf, sizeof(buf), "%.*f", y2_dec, (double)v);
                axis_label(out->obj, gutter_l + plot_w + 8, yy, gutter_r - 8, (int)fs + 2,
                           buf, fs, label_color, LV_TEXT_ALIGN_LEFT);
            }
        }
    }

    /* X 标签：逐主分隔，密了自动抽稀 */
    out->u.curve.xlabel_total_div = x_div;
    out->u.curve.xlabel_span = (points - 1) * out->u.curve.sample_ms / 1000;   /* 历史曲线下面按实际窗口改写 */
    out->u.curve.span_min = is_history ? span_min : 0;
    out->u.curve.x_clock = (strcmp(xf, "clock") == 0 || strcmp(xf, "clock_short") == 0);
    out->u.curve.x_left_grow = (!is_history && strcmp(xf, "elapsed") == 0);
    snprintf(out->u.curve.x_fmt, sizeof(out->u.curve.x_fmt), "%s", xf);
    if (out->u.curve.x_clock && !is_history && time(NULL) < 1700000000) {
        static int warned;

        if (!warned) {
            HMI_LOG("曲线: 系统时间未设置（早于 2024），绝对时间轴退回相对秒数");
            warned = 1;
        }
        out->u.curve.x_clock = 0;
    }

    if (show_labels) {
        int step = (int)x_step;
        int label_w = (out->u.curve.x_clock) ? 72 : 56;
        int lw;

        if (step <= 0) {
            int fit = x_div + 1;

            if (plot_w / label_w < fit) fit = plot_w / label_w;
            if (fit < 1) fit = 1;
            step = (x_div + 1 + fit - 1) / fit;
        }
        out->u.curve.xlabel_step = step;
        lw = label_w;

        for (int k = x_div; k >= 0; k -= step) {
            int xx = gutter_l + plot_w * k / x_div;
            int lx = xx - lw / 2;

            if (lx < 0) lx = 0;
            if (lx > (int)ww - lw) lx = (int)ww - lw;
            axis_label(out->obj, lx, (int32_t)hh - (uint32_t)gutter_b + 2, lw,
                       (int)fs + 2, "-", fs, label_color, LV_TEXT_ALIGN_CENTER);
            if (out->u.curve.nxlabels < 10) {
                out->u.curve.xlabels[out->u.curve.nxlabels] = lv_obj_get_child(out->obj, -1);
                out->u.curve.xlabel_k[out->u.curve.nxlabels] = x_div - k;
                out->u.curve.nxlabels++;
            }
        }
    }

    {
        lv_obj_t* tag = lv_label_create(out->obj);

        lv_label_set_text(tag, "浏览");
        lv_obj_set_style_text_font(tag, ui_font(fs >= 14 ? 14 : 12), 0);
        lv_obj_set_style_text_color(tag, ui_color_hex("#F2B84B"), 0);
        lv_obj_set_style_bg_opa(tag, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(tag, 0, 0);
        lv_obj_set_pos(tag, (int32_t)(gutter_l + plot_w - 96), 4);
        lv_obj_add_flag(tag, LV_OBJ_FLAG_HIDDEN);
        out->u.curve.browse_tag = tag;
    }

    if (is_history) {
        lv_obj_t* info = lv_label_create(out->obj);

        lv_label_set_text(info, "暂无历史数据");
        lv_obj_set_style_text_font(info, ui_font(14), 0);
        lv_obj_set_style_text_color(info, ui_color_hex("#9AA7B5"), 0);
        lv_obj_set_style_bg_opa(info, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(info, 0, 0);
        lv_obj_align(info, LV_ALIGN_CENTER, 0, 0);
        out->u.curve.tinfo = info;

        /* 窗口 = span_min 分钟：把这份时间跨度折算成「几个采样一个图点」 */
        {
            long iv = 1000;
            int nch = 0;

            (void)trend_stat(ui_trend_dir(), out->u.curve.tgroup, NULL, &nch, (uint32_t*)&iv);
            out->u.curve.sample_ms = iv > 0 ? iv : 1000;
        }
        {
            long span_slots = out->u.curve.span_min * 60 * 1000 / out->u.curve.sample_ms;
            int eff = (span_slots < points) ? (int)span_slots : (int)points;

            /* 窗口比图点还窄时（如 1 分钟 / 1 秒采样 = 60 个采样、却给了 150 个点），
             * 把图点数降到窗口能容纳的数量，否则 bucket 只能取 1，窗口会被撑大到 150 秒 */
            if (eff < 2) eff = 2;
            if (eff != (int)points) lv_chart_set_point_count(out->u.curve.chart, (uint32_t)eff);
            out->u.curve.bucket = (int)(span_slots / eff);
            if (out->u.curve.bucket < 1) out->u.curve.bucket = 1;
            out->u.curve.xlabel_span = (long)eff * out->u.curve.bucket * out->u.curve.sample_ms / 1000;
        }
        (void)curve_history_reload(out);
        if (refresh > 0 && refresh <= 600000) {
            out->u.curve.trefresh_ms = (int)refresh;
            out->u.curve.ttimer = lv_timer_create(history_timer_cb, (uint32_t)refresh, out);
        }
    } else {
        out->u.curve.bucket = 1;
        out->u.curve.t_ref = (long)time(NULL);
        curve_update_labels(out);
        out->u.curve.curve_timer = lv_timer_create(curve_sample_cb, (uint32_t)out->u.curve.sample_ms, out);
        if (!out->u.curve.curve_timer) {
            snprintf(err, errsz, "曲线采样定时器创建失败");
            return -1;
        }
    }
    return 0;
}

int build_realtime_curve(const hmi_json_t* props, long x, long y, long ww, long hh,
                         widget_t* out, char* err, size_t errsz)
{
    return build_curve(props, x, y, ww, hh, out, err, errsz, 0);
}

/* ── frame_anim：序列帧 ──
 * 帧图片复用 state_image 的 state_img_t（像素缓冲由 page_mgr 拆页时释放）；
 * 建控件时把帧全部加载完——播放到一半才发现坏图，比加载期报错难查。 */
/* ── xy_curve：X/Y 两路实时采样，画成散点（37 §6.6） ──
 * 只做实时累积，不做"从历史文件取一段 XY"——两通道的时间对齐不可靠（44 §3.4）。 */
static int xy_src_parse(const hmi_json_t* src, const char* what, widget_t* out,
                        int slot, char* err, size_t errsz)
{
    long tag;
    const char* ex;

    if (hmi_json_type(src) != HMI_JSON_OBJ) {
        snprintf(err, errsz, "%s 缺 {tag|expr}", what);
        return -1;
    }
    tag = hmi_json_int(hmi_json_get(src, "tag"), -1);
    ex = hmi_json_str(hmi_json_get(src, "expr"), NULL);
    if (ex && *ex) {
        char eerr[160];

        if (tag >= 0) {
            snprintf(err, errsz, "%s 同时给了 tag 与 expr，二选一", what);
            return -1;
        }
        if (expr_compile(ex, &out->u.curve.curve_expr[slot], eerr, sizeof(eerr)) != 0) {
            snprintf(err, errsz, "%s 的 expr 编译失败：%s", what, eerr);
            return -1;
        }
        out->u.curve.curve_has_expr[slot] = 1;
        out->u.curve.curve_tags[slot] = -1;
        return 0;
    }
    if (!tagdb_valid(tag)) {
        snprintf(err, errsz, "%s 既没有 expr，tag %ld 也不存在", what, tag);
        return -1;
    }
    out->u.curve.curve_tags[slot] = (int)tag;
    return 0;
}

static int xy_src_eval(widget_t* w, int slot, double* out)
{
    if (w->u.curve.curve_has_expr[slot]) {
        ex_val_t v;

        if (expr_eval(&w->u.curve.curve_expr[slot], &v) != 0 || v.type == EX_V_STR) return -1;
        *out = v.n;
        return 0;
    }
    *out = tagdb_num(w->u.curve.curve_tags[slot]);
    return 0;
}

static void xy_sample_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);
    double xv, yv;

    if (!w || !w->u.curve.chart) return;
    if (xy_src_eval(w, 0, &xv) != 0 || xy_src_eval(w, 1, &yv) != 0) return;
    lv_chart_set_next_value2(w->u.curve.chart, w->u.curve.series[0],
                             (int32_t)lround(xv), (int32_t)lround(yv));
}

int build_xy_curve(const hmi_json_t* props, long x, long y, long ww, long hh,
                   widget_t* out, char* err, size_t errsz)
{
    long sample_ms = hmi_json_int(hmi_json_get(props, "sample_ms"), 1000);
    long span_s = hmi_json_int(hmi_json_get(props, "span_s"), 60);
    int dot = hmi_json_bool(hmi_json_get(props, "dot"), 1);
    int line = hmi_json_bool(hmi_json_get(props, "line"), 0);
    int xmin = (int)hmi_json_int(hmi_json_get(props, "x_min"), 0);
    int xmax = (int)hmi_json_int(hmi_json_get(props, "x_max"), 100);
    int ymin = (int)hmi_json_int(hmi_json_get(props, "y_min"), 0);
    int ymax = (int)hmi_json_int(hmi_json_get(props, "y_max"), 100);
    int grid = hmi_json_bool(hmi_json_get(props, "grid"), 1);
    int x_div = (int)hmi_json_int(hmi_json_get(props, "x_div"), 4);
    int y_div = (int)hmi_json_int(hmi_json_get(props, "y_div"), 4);
    int x_minor = (int)hmi_json_int(hmi_json_get(props, "x_minor"), 0);
    int y_minor = (int)hmi_json_int(hmi_json_get(props, "y_minor"), 0);
    int x_dec = (int)hmi_json_int(hmi_json_get(props, "x_decimals"), 0);
    int y_dec = (int)hmi_json_int(hmi_json_get(props, "y_decimals"), 0);
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 14);
    const char* axis_color = hmi_json_str(hmi_json_get(props, "axis_color"), "#4A5A6A");
    const char* grid_color = hmi_json_str(hmi_json_get(props, "grid_color"), "#2F3D4C");
    const char* tick_color = hmi_json_str(hmi_json_get(props, "tick_color"), "#9AA7B5");
    const char* label_color = hmi_json_str(hmi_json_get(props, "label_color"), "#C8D2DC");
    const int gutter_l = 34, gutter_b = 18;   /* 左列放 Y 量程、底行放 X 量程 */
    int plot_w, plot_h, pts;
    lv_obj_t* box, * ch;
    char buf[24];

    if (!ui_color_valid(axis_color) || !ui_color_valid(grid_color) ||
        !ui_color_valid(tick_color) || !ui_color_valid(label_color)) {
        snprintf(err, errsz, "xy_curve 有非法颜色");
        return -1;
    }
    if (xmax <= xmin || ymax <= ymin) {
        snprintf(err, errsz, "xy_curve 量程非法（x %d..%d，y %d..%d）",
                 xmin, xmax, ymin, ymax);
        return -1;
    }
    if (x_div < 1) x_div = 1;
    if (x_div > 10) x_div = 10;
    if (y_div < 1) y_div = 1;
    if (y_div > 10) y_div = 10;
    if (x_minor < 0) x_minor = 0;
    if (y_minor < 0) y_minor = 0;
    if (fs < 10) fs = 10;
    if (fs > 32) fs = 32;
    plot_w = (int)ww - gutter_l - 2;
    plot_h = (int)hh - gutter_b - 2;
    if (plot_w < 40 || plot_h < 30) {
        snprintf(err, errsz, "xy_curve 框太小（绘图区需至少 40x30）");
        return -1;
    }
    if (xy_src_parse(hmi_json_get(props, "x"), "x", out, 0, err, errsz) != 0) return -1;
    if (xy_src_parse(hmi_json_get(props, "y"), "y", out, 1, err, errsz) != 0) return -1;
    if (sample_ms < 200) sample_ms = 200;
    pts = (int)(span_s * 1000 / sample_ms);
    if (pts < 2) pts = 2;
    if (pts > 300) pts = 300;

    box = lv_obj_create(cur_parent());
    if (!box) return -1;
    place(box, x, y, ww, hh);
    lv_obj_remove_flag(box, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_opa(box, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(box, 0, 0);

    /* 网格先画，chart 后建（在它上面），数据点就不会被网格盖住 */
    if (grid) {
        for (int k = 0; k <= y_div; k++) {
            axis_line(box, gutter_l, 2 + plot_h * k / y_div, plot_w, 1, grid_color);
            for (int m = 1; m <= y_minor && k < y_div; m++)
                axis_line(box, gutter_l,
                          2 + plot_h * k / y_div + plot_h * m / (y_div * (y_minor + 1)),
                          plot_w, 1, "#22303C");
        }
        for (int k = 0; k <= x_div; k++) {
            axis_line(box, gutter_l + plot_w * k / x_div, 2, 1, plot_h, grid_color);
            for (int m = 1; m <= x_minor && k < x_div; m++)
                axis_line(box, gutter_l + plot_w * k / x_div +
                          plot_w * m / (x_div * (x_minor + 1)), 2, 1, plot_h, "#22303C");
        }
    }
    /* 轴线 + 刻度 */
    axis_line(box, gutter_l - 2, 2, 1, plot_h, axis_color);
    axis_line(box, gutter_l - 2, 2 + plot_h, plot_w + 2, 1, axis_color);
    for (int k = 0; k <= y_div; k++)
        axis_line(box, gutter_l - 6, 2 + plot_h * k / y_div, 5, 1, tick_color);
    for (int k = 0; k <= x_div; k++)
        axis_line(box, gutter_l + plot_w * k / x_div, 3 + plot_h, 1, 4, tick_color);

    /* 量程标签：Y 在左列（上 = max），X 在底行（左 = min） */
    for (int k = 0; k <= y_div; k++) {
        double v = (double)ymax - (double)(ymax - ymin) * (double)k / (double)y_div;
        int yy = 2 + plot_h * k / y_div - fs / 2;

        if (yy < 0) yy = 0;
        if (yy > 2 + plot_h - fs) yy = 2 + plot_h - fs;
        snprintf(buf, sizeof(buf), "%.*f", y_dec, v);
        axis_label(box, 0, yy, gutter_l - 8, fs + 2, buf, fs, label_color,
                   LV_TEXT_ALIGN_RIGHT);
    }
    for (int k = 0; k <= x_div; k++) {
        double v = (double)xmin + (double)(xmax - xmin) * (double)k / (double)x_div;
        int xx = gutter_l + plot_w * k / x_div - 30;
        int lx = xx;

        if (lx < 0) lx = 0;
        if (lx > (int)ww - 60) lx = (int)ww - 60;
        snprintf(buf, sizeof(buf), "%.*f", x_dec, v);
        axis_label(box, lx, (int)hh - gutter_b + 2, 60, fs + 2, buf, fs, label_color,
                   LV_TEXT_ALIGN_CENTER);
    }

    ch = lv_chart_create(box);
    if (!ch) return -1;
    lv_obj_set_pos(ch, gutter_l, 2);
    lv_obj_set_size(ch, plot_w, plot_h);
    lv_obj_remove_flag(ch, LV_OBJ_FLAG_SCROLLABLE);   /* 可滚动会吞掉 PRESSING 手势 */
    lv_obj_remove_flag(ch, LV_OBJ_FLAG_CLICKABLE);
    lv_chart_set_type(ch, LV_CHART_TYPE_SCATTER);
    lv_chart_set_point_count(ch, (uint32_t)pts);
    lv_chart_set_div_line_count(ch, 0, 0);
    lv_chart_set_range(ch, LV_CHART_AXIS_PRIMARY_X, (int32_t)xmin, (int32_t)xmax);
    lv_chart_set_range(ch, LV_CHART_AXIS_PRIMARY_Y, (int32_t)ymin, (int32_t)ymax);
    lv_obj_set_style_bg_opa(ch, LV_OPA_TRANSP, LV_PART_MAIN);
    lv_obj_set_style_pad_all(ch, 0, LV_PART_MAIN);
    lv_obj_set_style_border_width(ch, 0, LV_PART_MAIN);
    /* 线的颜色/不透明度显式给：散点图默认主题没给 PART_ITEMS 的 line_opa */
    lv_obj_set_style_line_width(ch, line ? 2 : 0, LV_PART_ITEMS);
    lv_obj_set_style_line_color(ch, ui_color_hex("#4EA1FF"), LV_PART_ITEMS);
    lv_obj_set_style_line_opa(ch, LV_OPA_COVER, LV_PART_ITEMS);
    /* 点的宽高都要给：height=0 的点是画不出来的（0x3 的方块） */
    lv_obj_set_style_size(ch, dot ? 3 : 0, dot ? 3 : 0, LV_PART_INDICATOR);

    out->u.curve.series[0] = lv_chart_add_series(ch, ui_color_hex("#4EA1FF"),
                                         LV_CHART_AXIS_PRIMARY_Y);
    if (!out->u.curve.series[0]) return -1;
    out->u.curve.nseries = 1;
    out->u.curve.chart = ch;
    out->obj = box;
    out->type = W_XY_CURVE;
    out->u.curve.sample_ms = sample_ms;
    out->min = ymin;
    out->max = ymax;
    out->u.curve.xy_xmin = xmin;
    out->u.curve.xy_xmax = xmax;

    out->u.curve.curve_timer = lv_timer_create(xy_sample_cb, (uint32_t)sample_ms, out);
    if (!out->u.curve.curve_timer) {
        snprintf(err, errsz, "xy_curve 采样定时器创建失败");
        return -1;
    }
    return 0;
}
