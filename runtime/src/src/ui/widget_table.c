/*
 * widget_table.c — 自由表格（行容器 + 单元格 label，表头换底色）
 */
#include "widgets_internal.h"
#include "ui_font.h"
#include "core/i18n.h"
#include "hmi_log.h"

#include <stdio.h>
#include <string.h>

int build_table(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out, char* err, size_t errsz)
{
    /* 契约 table.rows / cols 的缺省是 3×3：手写工程不写这两个键也要能建页，
     * 而不是整页被拒（旧行为是 0 → 越界 → -1）。 */
    int rows = (int)hmi_json_int(hmi_json_get(props, "rows"), 3);
    int cols = (int)hmi_json_int(hmi_json_get(props, "cols"), 3);
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 14);
    int header = hmi_json_bool(hmi_json_get(props, "header"), 0);
    const hmi_json_t* cells = hmi_json_get(props, "cells");
    lv_obj_t* box;
    int rh, cw, colw[8];
    size_t n;

    if (rows < 1 || rows > 10 || cols < 1 || cols > 8) {
        snprintf(err, errsz, "table 的 rows/cols 越界（1..10 / 1..8）");
        return -1;
    }
    if (fs < 10 || fs > 32) {
        snprintf(err, errsz, "table 的 font_size %d 越界（10..32）", fs);
        return -1;
    }

    box = lv_obj_create(cur_parent());
    if (!box) {
        snprintf(err, errsz, "table 建控件失败");
        return -1;
    }
    place(box, x, y, ww, hh);
    lv_obj_remove_flag(box, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_color(box, ui_color_hex("#141C26"), 0);
    lv_obj_set_style_border_color(box, ui_color_hex("#3A4A5A"), 0);
    lv_obj_set_style_border_width(box, 1, 0);
    lv_obj_set_style_radius(box, 4, 0);
    lv_obj_set_style_pad_all(box, 0, 0);

    /* 行高：给了 row_h 就按它（超框则回落到等分），否则等分 */
    {
        int want = (int)hmi_json_int(hmi_json_get(props, "row_h"), 0);

        rh = (want > 0 && want * rows <= (int)hh) ? want : (int)hh / rows;
    }
    /* 列宽：给了 col_w[] 且个数、数值都合法就按它排（右侧允许留白），否则等分 */
    {
        const hmi_json_t* cwarr = hmi_json_get(props, "col_w");
        int sum = 0;

        cw = (int)ww / cols;
        for (int c = 0; c < cols; c++) colw[c] = cw;
        if (hmi_json_type(cwarr) == HMI_JSON_ARR &&
            hmi_json_count(cwarr) == (size_t)cols) {
            int ok = 1;

            for (int c = 0; c < cols; c++) {
                colw[c] = (int)hmi_json_int(hmi_json_at(cwarr, (size_t)c), 0);
                if (colw[c] < 8) ok = 0;
                sum += colw[c];
            }
            if (!ok || sum > (int)ww) {
                for (int c = 0; c < cols; c++) colw[c] = cw;
            }
        }
    }

    for (int r = 0; r < rows; r++) {
        lv_obj_t* row = lv_obj_create(box);

        if (!row) {
            snprintf(err, errsz, "table 建行失败（第 %d 行）", r);
            return -1;
        }
        lv_obj_set_pos(row, 0, (int32_t)(r * rh));
        lv_obj_set_size(row, (int32_t)ww, (int32_t)rh);
        lv_obj_remove_flag(row, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_set_style_radius(row, 0, 0);
        lv_obj_set_style_border_width(row, 0, 0);
        lv_obj_set_style_pad_all(row, 0, 0);
        /* 表头行与隔行：靠底色区分，不去改 lv_table 的逐格样式 */
        if (header && r == 0) {
            lv_obj_set_style_bg_color(row, ui_color_hex("#243240"), 0);
            lv_obj_set_style_bg_opa(row, LV_OPA_COVER, 0);
        } else {
            lv_obj_set_style_bg_opa(row, (r % 2) ? LV_OPA_30 : LV_OPA_TRANSP, 0);
            lv_obj_set_style_bg_color(row, ui_color_hex("#1B2530"), 0);
        }

        int cell_x = 0;

        for (int c = 0; c < cols; c++) {
            lv_obj_t* lb = lv_label_create(row);

            if (!lb) {
                snprintf(err, errsz, "table 建单元格失败（%d,%d）", r, c);
                return -1;
            }
            lv_obj_set_pos(lb, (int32_t)(cell_x + 6), (int32_t)((rh - fs) / 2));
            lv_obj_set_size(lb, (int32_t)(colw[c] - 12), (int32_t)fs + 4);
            cell_x += colw[c];
            lv_label_set_long_mode(lb, LV_LABEL_LONG_DOT);
            lv_label_set_text(lb, "");
            lv_obj_set_style_text_font(lb, ui_font(fs), 0);
            lv_obj_set_style_text_color(lb,
                ui_color_hex(header && r == 0 ? "#E6EDF3" : "#9AA7B5"), 0);
            lv_obj_set_style_bg_opa(lb, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(lb, 0, 0);
            lv_obj_set_style_pad_all(lb, 0, 0);
            out->u.table.cell[r * 8 + c] = lb;
        }
    }

    /* 静态文字：cells 里带 text 的项 */
    n = hmi_json_count(cells);
    for (size_t i = 0; i < n; i++) {
        const hmi_json_t* cell = hmi_json_at(cells, i);
        long r = hmi_json_int(hmi_json_get(cell, "r"), -1);
        long c = hmi_json_int(hmi_json_get(cell, "c"), -1);
        const char* text = hmi_json_str(hmi_json_get(cell, "text"), NULL);

        if (r < 0 || r >= rows || c < 0 || c >= cols) {
            snprintf(err, errsz, "table 的 cells[%zu] 行列越界（%ld,%ld）", i, r, c);
            return -1;
        }
        if (text) {
            char tb[160];

            lv_label_set_text(out->u.table.cell[r * 8 + c], i18n_text(text, tb, sizeof(tb)));
        }
    }

    out->obj = box;
    out->type = W_TABLE;
    return 0;
}
