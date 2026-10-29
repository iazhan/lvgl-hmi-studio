/*
 * widget_recipe.c — 配方表（ADR-0009）
 *
 * 行窗口化 + 滚动（同 history_table）：只建「可见行 + 2」个行容器，滚动时改起始行重填。
 * 单元格点击用编码在 user_data 里的 (窗口行, 列) 定位：
 *   成员值列（editable）→ 数字键盘；name 列（editable）→ ASCII 文本键盘；其它 → 下载该配方。
 */
#include "widget_recipe.h"
#include "core/recipe.h"
#include "core/audit.h"
#include "core/auth.h"
#include "core/i18n.h"
#include "ui_page.h"
#include "ui_keypad.h"
#include "ui_font.h"
#include "hmi_log.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define RC_MAX_COL 8

typedef struct {
    int gi;                       /* 组下标 */
    int ncol;
    int colkey[RC_MAX_COL];       /* -1 = name，-2 = desc，>=0 = 成员序号 */
    int colx[RC_MAX_COL];         /* 每列的左边界（点行时按横坐标判列） */
    int colw[RC_MAX_COL];
    int coled[RC_MAX_COL];
    int coldec[RC_MAX_COL];
    int row_h, vis, first, total;
    int show_unit;
    int sel;                      /* 选中的行（-1 = 没选中）：第一次点只选中，再点才编辑/下载 */
    int edit_idx, edit_col;       /* 正在编辑的单元格（键盘回调里用，取消时留着无害） */
    lv_obj_t* rows;
    lv_obj_t* spacer;
} recipe_priv_t;

#define RP(w) ((recipe_priv_t*)(w)->cdata)

/* ── 取值 / 文字 ── */
static void cell_text(recipe_priv_t* p, int idx, int c, char* out, size_t outsz)
{
    const recipe_group_t* g = recipe_at(p->gi);
    int key = p->colkey[c];

    out[0] = '\0';
    if (!g || idx < 0 || idx >= g->nitems) return;
    if (key == -1) {
        snprintf(out, outsz, "%s", g->meta[idx].name);
        return;
    }
    if (key == -2) {
        snprintf(out, outsz, "%s", g->meta[idx].desc);
        return;
    }
    if (key >= 0 && key < g->nmembers) {
        double v;

        if (g->is_str[key]) {          /* 字符串成员：原样显示，不做数值格式化 */
            snprintf(out, outsz, "%s",
                     g->svals + (((size_t)idx * (size_t)g->nmembers + (size_t)key) * RCP_STR_MAX));
            return;
        }
        v = g->vals[idx * g->nmembers + key];
        char unit[16] = "";

        if (p->show_unit) {
            const tag_def_t* t = NULL;

            /* 单位在 tag_def_t 上；这里没有 project 指针，靠 tagdb 只能拿到值，
             * 所以单位由 build 时缓存进 colw 之外的单列数组 */
            (void)t;
        }
        snprintf(out, outsz, "%.*f%s", p->coldec[c], v, unit);
    }
}

static void rp_fill(widget_t* w)
{
    recipe_priv_t* p = RP(w);
    const recipe_group_t* g = recipe_at(p->gi);

    if (!p || !p->rows) return;
    p->total = g ? g->nitems : 0;
    {
        int max_first = p->total - p->vis;

        if (max_first < 0) max_first = 0;
        if (p->first > max_first) p->first = max_first;
        if (p->first < 0) p->first = 0;
    }
    if (p->spacer)
        lv_obj_set_height(p->spacer, (int32_t)(p->total > 0 ? p->total * p->row_h : p->row_h));
    for (int i = 0; i < p->vis; i++) {
        lv_obj_t* row = lv_obj_get_child(p->rows, (int32_t)i);
        int idx = p->first + i;

        if (!row) continue;
        if (idx < 0 || idx >= p->total) {
            lv_obj_add_flag(row, LV_OBJ_FLAG_HIDDEN);
            continue;
        }
        lv_obj_remove_flag(row, LV_OBJ_FLAG_HIDDEN);
        lv_obj_set_y(row, (int32_t)(idx * p->row_h));
        /* 两种高亮：当前配方（深蓝 + 左侧竖条）与选中行（浅蓝 + 边框） */
        {
            int is_cur = (g && idx == g->current);
            int is_sel = (idx == p->sel);
            lv_obj_t* bar = lv_obj_get_child(row, (int32_t)p->ncol);

            lv_obj_set_style_bg_color(row, ui_color_hex(is_cur ? "#1F3550"
                                           : is_sel ? "#1A2A3A"
                                           : (i & 1) ? "#17212B" : "#141C26"), 0);
            lv_obj_set_style_border_width(row, is_sel ? 1 : 0, 0);
            lv_obj_set_style_border_color(row, ui_color_hex("#2F70D0"), 0);
            if (bar) {
                if (is_cur) lv_obj_remove_flag(bar, LV_OBJ_FLAG_HIDDEN);
                else lv_obj_add_flag(bar, LV_OBJ_FLAG_HIDDEN);
            }
        }
        for (int c = 0; c < p->ncol; c++) {
            lv_obj_t* lb = lv_obj_get_child(row, (int32_t)c);
            char buf[64];

            if (!lb) continue;
            cell_text(p, idx, c, buf, sizeof(buf));
            lv_label_set_text(lb, buf);
        }
    }
}

/* ── 编辑 / 下载 ── */
static void rp_save(widget_t* w)
{
    recipe_priv_t* p = RP(w);
    char err[128];

    if (!p) return;
    if (recipe_save(p->gi, err, sizeof(err)) != 0)
        HMI_LOG("recipe: 保存失败：%s", err);
    rp_fill(w);
}

static void recipe_str_commit(void* ud, const char* s)
{
    widget_t* w = (widget_t*)ud;
    recipe_priv_t* p = RP(w);

    if (!p || p->edit_idx < 0) return;
    if (recipe_set_str(p->gi, p->edit_idx, p->colkey[p->edit_col], s) != 0) return;
    rp_save(w);
}

static void recipe_value_commit(void* ud, double v)
{
    widget_t* w = (widget_t*)ud;
    recipe_priv_t* p = RP(w);

    if (!p || p->edit_idx < 0) return;
    if (recipe_set_value(p->gi, p->edit_idx, p->colkey[p->edit_col], v) != 0) return;
    rp_save(w);
}

static void recipe_name_commit(void* ud, const char* s)
{
    widget_t* w = (widget_t*)ud;
    recipe_priv_t* p = RP(w);

    if (!p || p->edit_idx < 0) return;
    if (recipe_set_name(p->gi, p->edit_idx, s) != 0) return;
    rp_save(w);
}

static void cell_click(widget_t* w, int row_i, int c);

/* 行点击：按点击横坐标落在哪一列，再走单元格逻辑 */
static void row_click_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    recipe_priv_t* p = RP(w);
    lv_obj_t* row = lv_event_get_current_target_obj(e);
    lv_indev_t* indev = lv_indev_active();
    lv_point_t pt = { 0, 0 };
    lv_area_t a;
    int lx, c = 0;

    if (!p || !row) return;
    if (indev) lv_indev_get_point(indev, &pt);
    lv_obj_get_coords(row, &a);
    lx = (int)pt.x - (int)a.x1;
    for (int k = 1; k < p->ncol; k++)
        if (lx >= p->colx[k]) c = k;
    cell_click(w, (int)(intptr_t)lv_obj_get_user_data(row), c);
}

/* 当前用户能否改这个表（operate 组掩码 + edit_recipe 命名位）；不能时只允许选中 */
static int s_sel_only_is_denied(widget_t* w, char* err, size_t errsz)
{
    (void)err;
    (void)errsz;
    return w && (!auth_can(w->poper) || !auth_perm(PERM_EDIT_RECIPE));
}

/* 单元格点击：window_row 是窗口内的行号，c 是列号 */
static void cell_click(widget_t* w, int row_i, int c)
{
    recipe_priv_t* p = RP(w);
    int idx;
    const recipe_group_t* g;
    keypad_req_t req;
    char err[128];

    if (!p || c < 0 || c >= p->ncol) return;
    idx = p->first + row_i;
    g = recipe_at(p->gi);
    if (!g || idx < 0 || idx >= g->nitems) return;

    /* 能看不能改：没有 operate 权限时，选中可以，编辑与下载一律拒绝（不静默） */
    if (s_sel_only_is_denied(w, err, sizeof(err))) {
        if (p->sel != idx) {
            p->sel = idx;
            rp_fill(w);
            return;
        }
        HMI_LOG("recipe: 拒绝修改（当前用户 %s，缺 operate 或 edit_recipe 权限）",
                auth_user_name() ? auth_user_name() : "未登录");
        audit_denied("recipe-edit", "operate/edit_recipe");
        toast_show(i18n_rt("toast.no_perm"));
        return;
    }
    /* 两段式：第一次点只选中该行，再点同一行才编辑 / 下载（防误触） */
    if (p->sel != idx) {
        p->sel = idx;
        rp_fill(w);
        return;
    }
    p->edit_idx = idx;
    p->edit_col = c;
    memset(&req, 0, sizeof(req));
    if (p->coled[c] && p->colkey[c] >= 0 && g->is_str[p->colkey[c]]) {
        req.mode = KEYPAD_TEXT;
        req.title = "配方值（文本）";
        req.text = g->svals + (((size_t)idx * (size_t)g->nmembers +
                                (size_t)p->colkey[c]) * RCP_STR_MAX);
        req.max_len = RCP_STR_MAX - 1;
        req.on_commit_text = recipe_str_commit;
        req.ud = w;
        if (ui_keypad_open(&req) != 0) HMI_LOG("recipe: 键盘打不开");
        return;
    }
    if (p->coled[c] && p->colkey[c] >= 0) {
        req.mode = KEYPAD_NUM;
        req.title = "配方值";
        req.value = g->vals[idx * g->nmembers + p->colkey[c]];
        req.decimals = p->coldec[c];
        req.on_commit_num = recipe_value_commit;
        req.ud = w;
        if (ui_keypad_open(&req) != 0) HMI_LOG("recipe: 键盘打不开");
        return;
    }
    if (p->coled[c] && p->colkey[c] == -1) {
        req.mode = KEYPAD_TEXT;
        req.title = "配方名（A-Z 0-9 . - _）";
        req.text = g->meta[idx].name;
        req.max_len = RCP_NAME_MAX - 1;
        req.on_commit_text = recipe_name_commit;
        req.ud = w;
        if (ui_keypad_open(&req) != 0) HMI_LOG("recipe: 键盘打不开");
        return;
    }
    /* 其它位置：下载该配方并设为当前 */
    if (recipe_activate(p->gi, idx, err, sizeof(err)) != 0)
        HMI_LOG("recipe: %s", err);
    else
        rp_fill(w);
}

/* 滚动：改起始行重填（行窗口化） */
static void rp_scroll_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    recipe_priv_t* p = RP(w);
    int sy, first, max_first;

    if (!p || !p->rows) return;
    sy = (int)lv_obj_get_scroll_y(p->rows);
    first = (sy >= 0) ? sy / p->row_h : 0;
    max_first = p->total - p->vis;
    if (max_first < 0) max_first = 0;
    if (first > max_first) first = max_first;
    if (first < 0) first = 0;
    if (first == p->first) return;
    p->first = first;
    rp_fill(w);
}

void recipe_table_refresh(widget_t* w)
{
    if (!w || !w->cdata || w->type != W_RECIPE_TABLE) return;
    rp_fill(w);
}

void recipe_widget_dispose(widget_t* w)
{
    if (!w || !w->cdata || w->type != W_RECIPE_TABLE) return;
    free(w->cdata);
    w->cdata = NULL;
}

int recipe_table_build(const hmi_json_t* props, long x, long y, long ww, long hh,
                       widget_t* out, char* err, size_t errsz)
{
    const hmi_json_t* src = hmi_json_get(props, "source");
    const hmi_json_t* cols = hmi_json_get(props, "columns");
    const char* group = hmi_json_str(hmi_json_get(src, "recipe_group"), NULL);
    int row_h = (int)hmi_json_int(hmi_json_get(props, "row_height"), 26);
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 14);
    int scrollbar = hmi_json_bool(hmi_json_get(props, "scrollbar"), 1);
    int show_unit = hmi_json_bool(hmi_json_get(props, "show_unit"), 1);
    recipe_priv_t* p;
    lv_obj_t* box, * hdr, * view;
    int ncol, body_h, cw_sum = 0;
    /* 单元格高取字体行高：中文的字面比 em 高，按 fs+2 定高会切掉字底 */
    int cell_h = (int)lv_font_get_line_height(ui_font(fs)) + 2;

    (void)show_unit;
    if (!group || !*group) {
        snprintf(err, errsz, "recipe_table 缺 source.recipe_group");
        return -1;
    }
    if (hmi_json_type(cols) != HMI_JSON_ARR) {
        snprintf(err, errsz, "recipe_table 缺 columns 数组");
        return -1;
    }
    ncol = (int)hmi_json_count(cols);
    if (ncol < 1 || ncol > RC_MAX_COL) {
        snprintf(err, errsz, "recipe_table 的 columns 项数 %d 超出 1..%d", ncol, RC_MAX_COL);
        return -1;
    }
    p = calloc(1, sizeof(*p));
    if (!p) return -1;
    p->gi = recipe_find(group);
    if (p->gi < 0) {
        /* 引用了不存在的组：明确报错，别显示空表（现场会以为配方丢了） */
        snprintf(err, errsz, "recipe_table 引用的配方组 %s 不存在（看 recipes/ 目录）", group);
        free(p);
        return -1;
    }
    p->ncol = ncol;
    p->row_h = (row_h < 16) ? 16 : (row_h > 60) ? 60 : row_h;
    p->show_unit = show_unit;

    for (int c = 0; c < ncol; c++) {
        const hmi_json_t* col = hmi_json_at(cols, (size_t)c);
        const char* key = hmi_json_str(hmi_json_get(col, "key"), NULL);
        long w = hmi_json_int(hmi_json_get(col, "w"), 0);

        if (key && strcmp(key, "name") == 0) p->colkey[c] = -1;
        else if (key && strcmp(key, "desc") == 0) p->colkey[c] = -2;
        else if (key && key[0] >= '0' && key[0] <= '9') p->colkey[c] = atoi(key);
        else {
            snprintf(err, errsz, "recipe_table 第 %d 列的 key=%s 非法"
                                 "（name / desc / 成员序号）", c, key ? key : "(缺失)");
            free(p);
            return -1;
        }
        p->coled[c] = hmi_json_bool(hmi_json_get(col, "editable"), 0);
        p->coldec[c] = (int)hmi_json_int(hmi_json_get(col, "decimals"), 1);
        if (p->coled[c] && p->colkey[c] == -2) {
            snprintf(err, errsz, "recipe_table 第 %d 列是 desc，desc 不支持编辑"
                                 "（editable 只对 name 与成员值列有效）", c);
            free(p);
            return -1;
        }
        p->colw[c] = (w > 0) ? (int)w : 0;
        cw_sum += p->colw[c];
    }
    for (int c = 0; c < ncol; c++)
        if (p->colw[c] == 0)
            p->colw[c] = ((int)ww - cw_sum > 0) ? ((int)ww - cw_sum) / ncol : (int)ww / ncol;
    /* 列左边界：行点击按横坐标判列（列宽可能因 w 缺省而均分，这里统一算一遍） */
    {
        int cx = 0;

        for (int c = 0; c < ncol; c++) {
            p->colx[c] = cx;
            cx += p->colw[c];
        }
    }

    box = lv_obj_create(ui_cur_parent());
    if (!box) {
        free(p);
        return -1;
    }
    ui_place(box, x, y, ww, hh);
    lv_obj_remove_flag(box, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_color(box, ui_color_hex("#141C26"), 0);
    lv_obj_set_style_border_color(box, ui_color_hex("#3A4A5A"), 0);
    lv_obj_set_style_border_width(box, 1, 0);
    lv_obj_set_style_radius(box, 4, 0);
    lv_obj_set_style_pad_all(box, 0, 0);

    hdr = lv_obj_create(box);
    if (hdr) {
        lv_obj_set_pos(hdr, 0, 0);
        lv_obj_set_size(hdr, (int32_t)ww - 2, (int32_t)p->row_h);
        lv_obj_remove_flag(hdr, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_set_style_bg_color(hdr, ui_color_hex("#1B2530"), 0);
        lv_obj_set_style_bg_opa(hdr, LV_OPA_COVER, 0);
        lv_obj_set_style_border_width(hdr, 0, 0);
        lv_obj_set_style_radius(hdr, 0, 0);
        lv_obj_set_style_pad_all(hdr, 0, 0);
        for (int c = 0, cx = 0; c < ncol; cx += p->colw[c], c++) {
            const hmi_json_t* col = hmi_json_at(cols, (size_t)c);
            const char* label = hmi_json_str(hmi_json_get(col, "label"), NULL);
            lv_obj_t* lb = lv_label_create(hdr);
            char tbuf[96];

            if (!lb) continue;
            lv_label_set_text(lb, label ? i18n_text(label, tbuf, sizeof(tbuf))
                                        : (p->colkey[c] == -1 ? "配方名" : "值"));
            lv_obj_set_pos(lb, (int32_t)(cx + 6), (int32_t)(p->row_h - fs) / 2);
            lv_obj_set_style_text_font(lb, ui_font(fs), 0);
            lv_obj_set_style_text_color(lb, ui_color_hex("#9AA7B5"), 0);
            lv_obj_set_style_bg_opa(lb, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(lb, 0, 0);
            lv_obj_set_style_pad_all(lb, 0, 0);
        }
    }

    body_h = (int)hh - p->row_h - 2;
    if (body_h < p->row_h) {
        snprintf(err, errsz, "recipe_table 太矮：表头之后没有数据行空间");
        free(p);
        return -1;
    }
    p->vis = body_h / p->row_h + 2;
    if (p->vis < 2) p->vis = 2;
    if (p->vis > 16) p->vis = 16;

    view = lv_obj_create(box);
    if (!view) {
        free(p);
        return -1;
    }
    lv_obj_set_pos(view, 0, (int32_t)p->row_h);
    lv_obj_set_size(view, (int32_t)ww - 2, (int32_t)body_h);
    lv_obj_set_style_bg_color(view, ui_color_hex("#141C26"), 0);
    lv_obj_set_style_bg_opa(view, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(view, 0, 0);
    lv_obj_set_style_radius(view, 0, 0);
    lv_obj_set_style_pad_all(view, 0, 0);
    ui_scroll_area(view, scrollbar);

    for (int i = 0; i < p->vis; i++) {
        lv_obj_t* row = lv_obj_create(view);

        if (!row) {
            free(p);
            snprintf(err, errsz, "recipe_table 建第 %d 行失败", i);
            return -1;
        }
        lv_obj_set_pos(row, 0, (int32_t)(i * p->row_h));
        lv_obj_set_size(row, (int32_t)ww - 10, (int32_t)p->row_h);
        lv_obj_remove_flag(row, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_set_style_bg_opa(row, LV_OPA_COVER, 0);
        lv_obj_set_style_border_width(row, 0, 0);
        lv_obj_set_style_radius(row, 0, 0);
        lv_obj_set_style_pad_all(row, 0, 0);
        for (int c = 0, cx = 0; c < ncol; cx += p->colw[c], c++) {
            lv_obj_t* lb = lv_label_create(row);

            if (!lb) continue;
            lv_label_set_text(lb, "");
            lv_label_set_long_mode(lb, LV_LABEL_LONG_CLIP);
            lv_obj_set_pos(lb, (int32_t)(cx + 6), (int32_t)(p->row_h - cell_h) / 2);
            lv_obj_set_size(lb, (int32_t)(p->colw[c] - 8), (int32_t)cell_h);
            lv_obj_set_style_text_font(lb, ui_font(fs), 0);
            lv_obj_set_style_text_color(lb, ui_color_hex("#E6EDF3"), 0);
            lv_obj_set_style_bg_opa(lb, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(lb, 0, 0);
            lv_obj_set_style_pad_all(lb, 0, 0);
            lv_obj_add_flag(lb, LV_OBJ_FLAG_CLICKABLE);
            lv_obj_remove_flag(lb, LV_OBJ_FLAG_CLICKABLE);  /* 统一由行接收点击（见 row_click_cb） */
        }
        /* 行接收点击：按横坐标判列。绑在 label 上会在选中态重排后丢事件（label 位置随文字变化） */
        lv_obj_add_flag(row, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_user_data(row, (void*)(intptr_t)i);
        lv_obj_add_event_cb(row, row_click_cb, LV_EVENT_CLICKED, out);

        /* 当前配方的左侧竖条：放在最后一格之后，保证单元格下标仍是 0..ncol-1 */
        {
            lv_obj_t* bar = lv_obj_create(row);

            if (bar) {
                lv_obj_set_pos(bar, 0, 0);
                lv_obj_set_size(bar, 3, (int32_t)p->row_h);
                lv_obj_remove_flag(bar, LV_OBJ_FLAG_SCROLLABLE);
                lv_obj_remove_flag(bar, LV_OBJ_FLAG_CLICKABLE);
                lv_obj_set_style_bg_color(bar, ui_color_hex("#4EA1FF"), 0);
                lv_obj_set_style_bg_opa(bar, LV_OPA_COVER, 0);
                lv_obj_set_style_border_width(bar, 0, 0);
                lv_obj_set_style_radius(bar, 0, 0);
                lv_obj_set_style_pad_all(bar, 0, 0);
                lv_obj_add_flag(bar, LV_OBJ_FLAG_HIDDEN);
            }
        }
    }

    p->spacer = lv_obj_create(view);
    if (p->spacer) {
        lv_obj_set_pos(p->spacer, 0, 0);
        lv_obj_set_size(p->spacer, 1, (int32_t)p->row_h);
        lv_obj_remove_flag(p->spacer, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_set_style_bg_opa(p->spacer, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(p->spacer, 0, 0);
        lv_obj_add_flag(p->spacer, LV_OBJ_FLAG_IGNORE_LAYOUT);
    }

    out->obj = box;
    out->type = W_RECIPE_TABLE;
    out->cdata = p;
    p->rows = view;
    p->edit_idx = -1;
    p->sel = -1;
    lv_obj_add_event_cb(view, rp_scroll_cb, LV_EVENT_SCROLL, out);
    rp_fill(out);
    lv_obj_scroll_to_y(view, 0, LV_ANIM_OFF);
    return 0;
}
