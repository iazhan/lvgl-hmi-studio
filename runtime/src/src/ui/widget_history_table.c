/*
 * widget_history_table.c — history_table：趋势文件的表格式回看
 */
#include "widgets_internal.h"
#include "ui_font.h"
#include "core/i18n.h"
#include "data/trend_logger.h"
#include "data/trend_store.h"
#include "data/savecfg.h"
#include "hmi_log.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static int ht_cmp_ts_desc(const void* a, const void* b)
{
    const trend_rec_t* x = (const trend_rec_t*)a;
    const trend_rec_t* y = (const trend_rec_t*)b;

    if (x->ts < y->ts) return 1;
    if (x->ts > y->ts) return -1;
    return 0;
}

static lv_obj_t* ht_row_at(widget_t* w, int i)
{
    return w->u.ht.ht_rows ? lv_obj_get_child(w->u.ht.ht_rows, (int32_t)i) : NULL;
}

/* 把当前窗口（ht_first 起、ht_vis 行）摆到内容坐标并填文字 */
static void ht_fill_window(widget_t* w)
{
    if (!w || !w->u.ht.ht_rows) return;
    for (int i = 0; i < w->u.ht.ht_vis; i++) {
        lv_obj_t* row = ht_row_at(w, i);
        int idx = w->u.ht.ht_first + i;

        if (!row) continue;
        if (idx < 0 || idx >= w->u.ht.ht_total) {
            lv_obj_add_flag(row, LV_OBJ_FLAG_HIDDEN);
            continue;
        }
        lv_obj_remove_flag(row, LV_OBJ_FLAG_HIDDEN);
        lv_obj_set_y(row, (int32_t)(idx * w->u.ht.ht_row_h));
        for (int c = 0; c < w->u.ht.ht_ncol; c++) {
            lv_obj_t* lb = lv_obj_get_child(row, (int32_t)c);
            const trend_rec_t* rec;
            char buf[32];

            if (!lb) continue;
            rec = &w->u.ht.ht_recs[idx];                       /* ht_recs 已按时间从新到旧排好 */
            if (w->u.ht.ht_chidx[c] == -2) {
                struct tm tmv;
                time_t ts = (time_t)rec->ts;

                localtime_r(&ts, &tmv);
                snprintf(buf, sizeof(buf), "%02d:%02d:%02d",
                         tmv.tm_hour, tmv.tm_min, tmv.tm_sec);
            } else {
                snprintf(buf, sizeof(buf), "%.*f", w->u.ht.ht_dec[c],
                         (double)rec->v[w->u.ht.ht_chidx[c]]);
            }
            lv_label_set_text(lb, buf);
        }
    }
    if (w->u.ht.ht_total == 0) {
        lv_obj_t* row = ht_row_at(w, 0);
        lv_obj_t* lb = row ? lv_obj_get_child(row, 0) : NULL;

        if (row) lv_obj_remove_flag(row, LV_OBJ_FLAG_HIDDEN);
        if (lb) lv_label_set_text(lb, "暂无历史数据");
    }
}

static void ht_scroll_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    int sy, first, max_first;

    if (!w || !w->u.ht.ht_rows) return;
    sy = (int)lv_obj_get_scroll_y(w->u.ht.ht_rows);
    first = (sy >= 0) ? sy / w->u.ht.ht_row_h : 0;
    max_first = w->u.ht.ht_total - w->u.ht.ht_vis;
    if (max_first < 0) max_first = 0;
    if (first > max_first) first = max_first;
    if (first < 0) first = 0;
    if (first == w->u.ht.ht_first) return;
    w->u.ht.ht_first = first;
    ht_fill_window(w);
}

void history_table_dispose(widget_t* w)
{
    /* 只认领自己的类型：widget_t 的字段是复用的，别的控件不该走到这里 */
    if (!w || w->type != W_HISTORY_TABLE) return;
    if (w->u.ht.ht_timer) {
        lv_timer_delete(w->u.ht.ht_timer);
        w->u.ht.ht_timer = NULL;
    }
    free(w->u.ht.ht_recs);
    w->u.ht.ht_recs = NULL;
    w->u.ht.ht_nrec = 0;
}

/* 一格：固定宽度、单行、超长裁掉（换行会把行高撑破） */
static lv_obj_t* ht_cell(lv_obj_t* parent, int x, int y, int w, int h, int fs,
                         const char* color, lv_text_align_t align)
{
    lv_obj_t* lb = lv_label_create(parent);

    if (!lb) return NULL;
    lv_label_set_text(lb, "");
    lv_label_set_long_mode(lb, LV_LABEL_LONG_CLIP);
    lv_obj_set_pos(lb, (int32_t)x, (int32_t)y);
    lv_obj_set_size(lb, (int32_t)w, (int32_t)h);
    lv_obj_set_style_text_font(lb, ui_font(fs), 0);
    lv_obj_set_style_text_color(lb, ui_color_hex(color), 0);
    lv_obj_set_style_text_align(lb, align, 0);
    lv_obj_set_style_bg_opa(lb, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(lb, 0, 0);
    lv_obj_set_style_pad_all(lb, 0, 0);
    return lb;
}

static lv_obj_t* ht_box(lv_obj_t* parent, int x, int y, int w, int h, const char* bg)
{
    lv_obj_t* o = lv_obj_create(parent);

    if (!o) return NULL;
    lv_obj_set_pos(o, (int32_t)x, (int32_t)y);
    lv_obj_set_size(o, (int32_t)w, (int32_t)h);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_color(o, ui_color_hex(bg), 0);
    lv_obj_set_style_bg_opa(o, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(o, 0, 0);
    lv_obj_set_style_radius(o, 0, 0);
    lv_obj_set_style_pad_all(o, 0, 0);
    return o;
}

/* 读一次趋势文件，把窗口内的记录收进控件自己的数组（最新的在前）。
 * 建页时调一次，refresh_ms 到了再调一次——两处共用，别写两遍。 */
static int ht_load_records(widget_t* w, const char* group, int max_rows, long last_min,
                           char* err, size_t errsz)
{
    trend_rec_t* recs = NULL;
    int nch = 0, iv = 0, nrec, keep = 0;
    time_t now;

    nrec = trend_read_all(ui_trend_dir(), group, &recs, &nch, &iv);
    if (nrec > 0 && recs) {
        now = time(NULL);
        for (int r = nrec - 1; r >= 0; r--) {
            if ((long)recs[r].ts + last_min * 60 < (long)now && keep > 0) break;
            keep++;
        }
    }
    if (keep > max_rows) keep = max_rows;
    free(w->u.ht.ht_recs);
    w->u.ht.ht_recs = NULL;
    w->u.ht.ht_nrec = 0;
    if (keep > 0) {
        w->u.ht.ht_recs = calloc((size_t)keep, sizeof(*w->u.ht.ht_recs));
        if (!w->u.ht.ht_recs) {
            free(recs);
            snprintf(err, errsz, "history_table 内存不足（%d 条记录）", keep);
            return -1;
        }
        for (int k = 0; k < keep; k++) w->u.ht.ht_recs[k] = recs[nrec - 1 - k];
        qsort(w->u.ht.ht_recs, (size_t)keep, sizeof(*w->u.ht.ht_recs), ht_cmp_ts_desc);
        w->u.ht.ht_nrec = keep;
    }
    w->u.ht.ht_total = keep;
    free(recs);
    (void)iv;
    return 0;
}

/* 重新读盘并就地刷新：保持用户当前的滚动位置（新记录是加在“上面”的） */
static void ht_refresh_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);
    int keep_first, max_first, old_total;
    char err[96];

    if (!w || !w->u.ht.ht_rows) return;
    keep_first = w->u.ht.ht_first;
    old_total = w->u.ht.ht_total;
    if (ht_load_records(w, w->u.ht.tgroup, w->u.ht.ht_max_rows, w->u.ht.ht_last_min,
                        err, sizeof(err)) != 0)
        return;
    /* 新记录是加在“上面”的：用户已经往下翻了，就把起始行跟着挪，画面里的内容不跳 */
    if (keep_first > 0) keep_first += w->u.ht.ht_total - old_total;
    if (w->u.ht.ht_spacer) {
        lv_obj_set_height(w->u.ht.ht_spacer, (int32_t)(w->u.ht.ht_total > 0
                            ? w->u.ht.ht_total * w->u.ht.ht_row_h : w->u.ht.ht_row_h));
    }
    max_first = w->u.ht.ht_total - w->u.ht.ht_vis;
    if (max_first < 0) max_first = 0;
    if (keep_first > max_first) keep_first = max_first;
    w->u.ht.ht_first = keep_first;
    ht_fill_window(w);
    lv_obj_scroll_to_y(w->u.ht.ht_rows, (int32_t)(keep_first * w->u.ht.ht_row_h), LV_ANIM_OFF);
}

int build_history_table(const hmi_json_t* props, long x, long y, long ww, long hh,
                        widget_t* out, char* err, size_t errsz)
{
    const hmi_json_t* src = hmi_json_get(props, "source");
    const hmi_json_t* cols = hmi_json_get(props, "columns");
    int row_h = (int)hmi_json_int(hmi_json_get(props, "row_height"), 26);
    int max_rows = (int)hmi_json_int(hmi_json_get(props, "max_rows"), 500);
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 14);
    int scrollbar = hmi_json_bool(hmi_json_get(props, "scrollbar"), 1);
    int refresh_ms = (int)hmi_json_int(hmi_json_get(props, "refresh_ms"), 5000);
    const char* group = hmi_json_str(hmi_json_get(src, "group"), NULL);
    long last_min = hmi_json_int(hmi_json_get(src, "last_min"), 30);
    int gch = 0;
    const int* gtags = NULL;
    int cw_sum = 0;
    lv_obj_t* box, * hdr, * view, * spacer;
    int ncol, body_h, vis, i;
    int cell_h, cell_y;

    if (!group || !*group) {
        snprintf(err, errsz, "history_table 缺 source.group");
        return -1;
    }
    if (hmi_json_type(cols) != HMI_JSON_ARR) {
        snprintf(err, errsz, "history_table 缺 columns 数组");
        return -1;
    }
    ncol = (int)hmi_json_count(cols);
    if (ncol < 1 || ncol > 8) {
        snprintf(err, errsz, "history_table 的 columns 项数 %d 超出 1..8", ncol);
        return -1;
    }
    if (row_h < 16) row_h = 16;
    if (row_h > 60) row_h = 60;
    if (fs < 10) fs = 10;
    if (fs > 28) fs = 28;
    if (last_min < 1) last_min = 1;
    /* 单元格高取字体行高：中文的字面比 em 高，按 fs+2 定高会切掉字底 */
    cell_h = (int)lv_font_get_line_height(ui_font(fs)) + 2;
    cell_y = (row_h - cell_h) / 2;
    if (cell_y < 0) cell_y = 0;
    if (trend_logger_group(group, &gch, &gtags) != 0 &&
        savecfg_group(group, &gch, &gtags) != 0) {
        snprintf(err, errsz, "history_table 的趋势组 %s 不存在（看 trends.json）", group);
        return -1;
    }

    /* 列：time 或某个变量 tag（必须是本组采集的通道） */
    for (int c = 0; c < ncol; c++) {
        const hmi_json_t* col = hmi_json_at(cols, (size_t)c);
        const hmi_json_t* key = hmi_json_get(col, "key");
        long w = hmi_json_int(hmi_json_get(col, "w"), 0);

        out->u.ht.ht_chidx[c] = -1;
        if (hmi_json_type(key) == HMI_JSON_STR &&
            strcmp(hmi_json_str(key, ""), "time") == 0) {
            out->u.ht.ht_chidx[c] = -2;                     /* -2 = 时间列 */
        } else {
            long tag = hmi_json_int(key, -1);
            int k;

            if (tag < 0) {
                snprintf(err, errsz, "history_table 第 %d 列的 key 只能是 time 或变量 tag"
                                     "（v1 不支持 level/name/state/ack）", c);
                return -1;
            }
            for (k = 0; k < gch; k++)
                if (gtags[k] == (int)tag) { out->u.ht.ht_chidx[c] = k; break; }
            if (out->u.ht.ht_chidx[c] < 0) {
                snprintf(err, errsz, "history_table 第 %d 列的 tag %ld 不在趋势组 %s 里",
                         c, tag, group);
                return -1;
            }
        }
        out->u.ht.ht_colw[c] = (w > 0) ? (int)w : 0;
        out->u.ht.ht_dec[c] = (int)hmi_json_int(hmi_json_get(col, "decimals"), 1);
        cw_sum += out->u.ht.ht_colw[c];
    }
    for (int c = 0; c < ncol; c++) {
        if (out->u.ht.ht_colw[c] == 0) {
            int left = (int)ww - cw_sum;

            out->u.ht.ht_colw[c] = (left > 0) ? left / ncol : (int)ww / ncol;
        }
    }
    out->u.ht.ht_ncol = ncol;

    /* 读一次趋势，只留窗口内的记录（滚动时不再读盘；refresh_ms 到了再重读） */
    out->u.ht.ht_max_rows = max_rows;
    out->u.ht.ht_last_min = last_min;
    if (ht_load_records(out, group, max_rows, last_min, err, errsz) != 0) return -1;

    box = ht_box(cur_parent(), 0, 0, (int)ww, (int)hh, "#141C26");
    if (!box) return -1;
    place(box, x, y, ww, hh);
    lv_obj_set_style_border_color(box, ui_color_hex("#3A4A5A"), 0);
    lv_obj_set_style_border_width(box, 1, 0);
    lv_obj_set_style_radius(box, 4, 0);

    /* 表头 */
    hdr = ht_box(box, 0, 0, (int)ww - 2, row_h, "#1B2530");
    if (hdr) {
        for (int c = 0, cx = 0; c < ncol; cx += out->u.ht.ht_colw[c], c++) {
            const hmi_json_t* col = hmi_json_at(cols, (size_t)c);
            const char* label = hmi_json_str(hmi_json_get(col, "label"), NULL);
            lv_obj_t* lb = ht_cell(hdr, cx + 6, cell_y, out->u.ht.ht_colw[c] - 8,
                                   cell_h, fs, "#9AA7B5", LV_TEXT_ALIGN_LEFT);

            if (lb) {
                char tb[96];

                if (label && *label)
                    lv_label_set_text(lb, i18n_text(label, tb, sizeof(tb)));
                else                    /* 没给列头：用运行时的通用词 */
                    lv_label_set_text(lb, i18n_rt(out->u.ht.ht_chidx[c] == -2 ? "table.time"
                                                                       : "table.value"));
            }
        }
    }

    /* 数据区：可上下滚动，滚动条画在右侧 */
    body_h = (int)hh - row_h - 2;
    if (body_h < row_h) {
        snprintf(err, errsz, "history_table 太矮：表头之后没有数据行空间");
        return -1;
    }
    vis = body_h / row_h + 2;                  /* 可见行 + 2 行预取 */
    if (vis < 2) vis = 2;
    if (vis > 16) vis = 16;
    out->u.ht.ht_vis = vis;
    out->u.ht.ht_row_h = row_h;
    out->u.ht.ht_first = 0;

    view = ht_box(box, 0, row_h, (int)ww - 2, body_h, "#141C26");
    if (!view) {
        snprintf(err, errsz, "history_table 建数据区失败");
        return -1;
    }
    ui_scroll_area(view, scrollbar);

    /* 只建可见的十几行 */
    for (i = 0; i < vis; i++) {
        lv_obj_t* row = ht_box(view, 0, i * row_h, (int)ww - 10, row_h,
                               (i & 1) ? "#17212B" : "#141C26");

        if (!row) {
            snprintf(err, errsz, "history_table 建第 %d 行失败", i);
            return -1;
        }
        lv_obj_remove_flag(row, LV_OBJ_FLAG_SCROLLABLE);
        for (int c = 0, cx = 0; c < ncol; cx += out->u.ht.ht_colw[c], c++)
            (void)ht_cell(row, cx + 6, cell_y, out->u.ht.ht_colw[c] - 8, cell_h, fs,
                          "#E6EDF3", LV_TEXT_ALIGN_LEFT);
    }

    /* 透明 spacer 撑起内容高度，滚动条才有行程 */
    spacer = lv_obj_create(view);
    if (spacer) {
        lv_obj_set_pos(spacer, 0, 0);
        lv_obj_set_size(spacer, 1, (int32_t)(out->u.ht.ht_total > 0 ? out->u.ht.ht_total * row_h : body_h));
        lv_obj_remove_flag(spacer, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_set_style_bg_opa(spacer, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(spacer, 0, 0);
        lv_obj_set_style_pad_all(spacer, 0, 0);
        lv_obj_add_flag(spacer, LV_OBJ_FLAG_IGNORE_LAYOUT);
    }

    out->obj = box;
    out->type = W_HISTORY_TABLE;
    out->u.ht.ht_rows = view;
    out->u.ht.ht_spacer = spacer;
    out->u.ht.ht_last_min = last_min;
    snprintf(out->u.ht.tgroup, sizeof(out->u.ht.tgroup), "%s", group);
    lv_obj_add_event_cb(view, ht_scroll_cb, LV_EVENT_SCROLL, out);
    ht_fill_window(out);
    lv_obj_scroll_to_y(view, 0, LV_ANIM_OFF);   /* LVGL 会自己滚到最后一个子对象，拉回顶部 */
    /* 与 history_curve 的 refresh_ms 同义：0 = 只读一次 */
    if (refresh_ms > 0) {
        if (refresh_ms < 200) refresh_ms = 200;
        out->u.ht.ht_refresh_ms = refresh_ms;
        out->u.ht.ht_timer = lv_timer_create(ht_refresh_cb, (uint32_t)refresh_ms, out);
        if (!out->u.ht.ht_timer) {
            snprintf(err, errsz, "history_table 刷新定时器创建失败");
            return -1;
        }
    }
    return 0;
}
