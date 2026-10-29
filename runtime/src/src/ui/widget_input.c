/*
 * widget_input.c — 需要用户选/填的：input / combobox / roller
 */
#include "widgets_internal.h"
#include "ui_font.h"
#include "core/i18n.h"
#include "hmi_log.h"

#include <stdio.h>
#include <string.h>

int build_input(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out, char* err, size_t errsz)
{
    lv_obj_t* o = lv_textarea_create(cur_parent());
    const char* unit = hmi_json_str(hmi_json_get(props, "unit"), "");
    const char* mode = hmi_json_str(hmi_json_get(props, "mode"), "num");
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 16);

    if (!o) return -1;
    if (strcmp(mode, "num") != 0 && strcmp(mode, "text") != 0 && strcmp(mode, "password") != 0) {
        snprintf(err, errsz, "input 的 mode=%s 非法（num/text/password）", mode);
        return -1;
    }
    out->input_text = (strcmp(mode, "num") != 0);      /* text/password：写字符串变量 */
    /* 写回时机（契约 input.commit）：ok = 只有按确认才写（默认）；
     * blur = 这次输入结束时也写（取消/失焦）。运行时没有真的"失焦"事件——
     * 键盘是模态的，所以"输入结束"就是最近似的语义。 */
    out->input_blur = (strcmp(hmi_json_str(hmi_json_get(props, "commit"), "ok"), "blur") == 0);
    out->has_min = (hmi_json_get(props, "min") != NULL);
    out->has_max = (hmi_json_get(props, "max") != NULL);
    out->min = (int)hmi_json_int(hmi_json_get(props, "min"), 0);
    out->max = (int)hmi_json_int(hmi_json_get(props, "max"), 0);
    out->decimals = (int)hmi_json_int(hmi_json_get(props, "decimals"), 0);
    out->max_len = (int)hmi_json_int(hmi_json_get(props, "max_len"), 12);
    if (out->input_text) {
        /* 文本模式：量程/小数位都不适用，改由 max_len 限长（1..64 字节） */
        if (out->max_len < 1 || out->max_len > 64) {
            snprintf(err, errsz, "input 的 max_len %d 越界（1..64）", out->max_len);
            return -1;
        }
        out->has_min = out->has_max = 0;
        out->decimals = 0;
        snprintf(out->unit, sizeof(out->unit), "%s", "");
    }
    if (strcmp(mode, "password") == 0) lv_textarea_set_password_mode(o, true);
    /* one_line 必须在 place 之前调：lv_textarea_set_one_line(true) 内部会把高度设成
     * LV_SIZE_CONTENT（见 lv_textarea.c 的 set_one_line），之后再 place 才拿得回控件高度。
     * 顺序反了的话，编辑器里调高度位图完全不动、只有选中框在变。 */
    lv_textarea_set_one_line(o, true);
    place(o, x, y, ww, hh);
    lv_textarea_set_text(o, "");
    lv_obj_set_style_text_font(o, ui_font(fs), 0);
    lv_obj_set_style_bg_color(o, ui_color_hex("#1B2530"), 0);
    lv_obj_set_style_bg_opa(o, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(o, 1, 0);
    lv_obj_set_style_border_color(o, ui_color_hex("#3A4A5A"), 0);
    lv_obj_set_style_radius(o, 4, 0);
    lv_obj_set_style_pad_all(o, 8, 0);
    lv_obj_set_style_text_color(o, ui_color_hex("#E6EDF3"), 0);
    /* 水平对齐（契约 input.align）：面板上那一行此前没有读者，选「居中 / 右对齐」
     * 位图不动。left 是缺省，不设样式（保持 textarea 原样）。
     *
     * 必须设在内部 label 上：textarea 排版时读的是 `lv_textarea_get_label(obj)`
     * 的 text_align（lv_textarea.c 的 draw 里 calculate_style_text_align），
     * 设在 textarea 本体上无效。 */
    {
        const char* align = hmi_json_str(hmi_json_get(props, "align"), "left");
        lv_text_align_t ta = LV_TEXT_ALIGN_LEFT;

        if (strcmp(align, "center") == 0) ta = LV_TEXT_ALIGN_CENTER;
        else if (strcmp(align, "right") == 0) ta = LV_TEXT_ALIGN_RIGHT;
        if (ta != LV_TEXT_ALIGN_LEFT) {
            lv_obj_t* lb = lv_textarea_get_label(o);

            lv_obj_set_style_text_align(o, ta, 0);
            if (lb) lv_obj_set_style_text_align(lb, ta, 0);
        }
    }
    /* 单行输入在更高的框里垂直居中：textarea 默认把文字排在内边距上，55 高的框里会贴顶 */
    {
        int lh = lv_font_get_line_height(ui_font(fs));
        int avail = (int)lv_obj_get_content_height(o);

        if (avail > lh) lv_obj_set_style_pad_top(o, 8 + (avail - lh) / 2, 0);
    }
    snprintf(out->unit, sizeof(out->unit), "%s", unit);
    out->obj = o;
    out->type = W_INPUT;
    return 0;
}

int build_combobox(const hmi_json_t* props, long x, long y, long ww, long hh,
                   widget_t* out)
{
    lv_obj_t* o = lv_dropdown_create(cur_parent());
    const hmi_json_t* opts = hmi_json_get(props, "options");
    const tag_def_t* ot = NULL;
    char joined[512];
    size_t n = 0, used = 0;

    if (!o) return -1;
    out->opt_tag = -1;
    if (hmi_json_get(props, "options_tag") != NULL) {
        int tid = (int)hmi_json_int(hmi_json_get(props, "options_tag"), -1);

        ot = project_tag(ui_proj_ctx, tid);
        if (!ot || ot->enum_count == 0) {
            HMI_LOG("widget: options_tag=%d 指向的变量没有 enum 定义", tid);
            return -1;
        }
        out->opt_tag = tid;
        n = (size_t)ot->enum_count;
    } else {
        if (hmi_json_type(opts) != HMI_JSON_ARR) return -1;
        n = hmi_json_count(opts);
    }
    if (n == 0 || n > 32) return -1;
    joined[0] = '\0';
    for (size_t i = 0; i < n; i++) {
        const char* str = ot ? ot->enums[i].text
                             : hmi_json_str(hmi_json_at(opts, i), NULL);
        char tb[96];
        int wrote;

        if (!str) return -1;
        /* 选项文字可以写 @id：静态 options 与变量 enum 同口径，拼成列表前先渲染 */
        str = i18n_text(str, tb, sizeof(tb));
        wrote = snprintf(joined + used, sizeof(joined) - used, "%s%s",
                         i ? "\n" : "", str);
        if (wrote < 0 || (size_t)wrote >= sizeof(joined) - used) return -1;
        used += (size_t)wrote;
    }
    place(o, x, y, ww, hh);
    lv_dropdown_set_options(o, joined);
    if (ot) {
        /* 选中项由变量当前值反查；没有匹配就落到第一项 */
        double v = tagdb_num(out->opt_tag);
        uint32_t sel = 0;

        for (int i = 0; i < ot->enum_count; i++)
            if (ot->enums[i].v == v) { sel = (uint32_t)i; break; }
        lv_dropdown_set_selected(o, sel);
    } else {
        /* 初始选中项（契约 combobox.selected）：越界钳到范围内。下标越界时 LVGL
         * 会把选中项设成"无"，下拉框显示空白——面板上改一个数字就得到空框。 */
        long sel = hmi_json_int(hmi_json_get(props, "selected"), 0);

        if (sel < 0) sel = 0;
        if (sel >= (long)n) sel = (long)n - 1;
        lv_dropdown_set_selected(o, (uint32_t)sel);
    }
    lv_obj_set_style_bg_color(o, ui_color_hex("#1B2530"), 0);
    lv_obj_set_style_border_width(o, 1, 0);
    lv_obj_set_style_border_color(o, ui_color_hex("#3A4A5A"), 0);
    /* 下拉框本体与展开列表都要单独给中文字体：默认主题字体只有 ASCII。
     * 箭头不能用 LV_SYMBOL（中文字体里没有该字形，会变方块），用 lv_line 画一个
     * 三角形——与字体无关，任何字号都画得出来。 */
    lv_dropdown_set_symbol(o, NULL);
    {
        /* 字号与文字色来自面板（契约 combobox.font_size / color）。此前这里写死
         * 16 / #E6EDF3，面板上那两行改了位图一点不动。 */
        int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 16);
        const char* color = hmi_json_str(hmi_json_get(props, "color"), "#E6EDF3");

        if (fs < 10) fs = 10;
        if (fs > 32) fs = 32;
        if (!ui_color_valid(color)) color = "#E6EDF3";
        lv_obj_set_style_text_font(o, ui_font(fs), 0);
        lv_obj_set_style_text_color(o, ui_color_hex(color), 0);
        {
            lv_obj_t* list = lv_dropdown_get_list(o);

            if (list) {
                lv_obj_set_style_text_font(list, ui_font(fs), 0);
                lv_obj_set_style_bg_color(list, ui_color_hex("#1B2530"), 0);
                lv_obj_set_style_text_color(list, ui_color_hex(color), 0);
            }
        }
    }
    {
        lv_obj_t* arrow = lv_line_create(o);

        if (arrow) {
            out->u.combo.arrow_pts[0].x = 0;
            out->u.combo.arrow_pts[0].y = 0;
            out->u.combo.arrow_pts[1].x = 10;
            out->u.combo.arrow_pts[1].y = 0;
            out->u.combo.arrow_pts[2].x = 5;
            out->u.combo.arrow_pts[2].y = 6;
            out->u.combo.arrow_pts[3].x = 0;
            out->u.combo.arrow_pts[3].y = 0;
            lv_line_set_points(arrow, out->u.combo.arrow_pts, 4);
            lv_obj_set_style_line_width(arrow, 2, 0);
            lv_obj_set_style_line_color(arrow, ui_color_hex("#9AA7B5"), 0);
            lv_obj_set_style_line_rounded(arrow, true, 0);
            lv_obj_align(arrow, LV_ALIGN_RIGHT_MID, -16, 1);
        }
    }
    out->obj = o;
    out->type = W_COMBOBOX;
    return 0;
}

/* ── state_image：数值 → 图片（37 §6.5） ── */
/* ── roller：滚轮选择（选班次、配方号这类固定选项） ──
 * 高度由 visible_rows × 行高算（LVGL 的滚轮行数就是高度），宽度用控件框的 w。 */
int build_roller(const hmi_json_t* props, long x, long y, long ww, long hh,
                 widget_t* out)
{
    const hmi_json_t* opts = hmi_json_get(props, "options");
    int rows = (int)hmi_json_int(hmi_json_get(props, "visible_rows"), 3);
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 16);
    char joined[512];
    size_t n, used = 0;
    lv_obj_t* o;

    if (hmi_json_type(opts) != HMI_JSON_ARR) return -1;
    n = hmi_json_count(opts);
    if (n == 0 || n > 32) return -1;
    joined[0] = '\0';
    for (size_t i = 0; i < n; i++) {
        const char* str = hmi_json_str(hmi_json_at(opts, i), NULL);
        char tb[96];
        int wrote;

        if (!str) return -1;
        str = i18n_text(str, tb, sizeof(tb));
        wrote = snprintf(joined + used, sizeof(joined) - used, "%s%s",
                         i ? "\n" : "", str);
        if (wrote < 0 || (size_t)wrote >= sizeof(joined) - used) return -1;
        used += (size_t)wrote;
    }
    if (rows < 3) rows = 3;
    if (rows > 7) rows = 7;
    if ((rows & 1) == 0) rows++;        /* 契约要求奇数行，选中行才在正中 */
    if (fs < 10) fs = 10;
    if (fs > 32) fs = 32;

    o = lv_roller_create(cur_parent());
    if (!o) return -1;
    lv_obj_set_pos(o, (int32_t)x, (int32_t)y);
    lv_obj_set_width(o, (int32_t)ww);
    lv_roller_set_options(o, joined, LV_ROLLER_MODE_NORMAL);
    lv_obj_set_style_text_font(o, ui_font(fs), 0);
    lv_obj_set_style_text_font(o, ui_font(fs), LV_PART_SELECTED);
    lv_obj_set_style_pad_all(o, 0, 0);
    lv_obj_set_style_border_width(o, 0, 0);
    lv_roller_set_visible_row_count(o, (uint32_t)rows);
    /* 高度以控件框为准：主题给 roller 的上下内边距会把高度撑大
     * （3 行 × 21px 实测 111px），不压回框高就会盖住邻居。 */
    if (hh > 0) {
        uint32_t lh = lv_font_get_line_height(ui_font(fs));

        lv_obj_set_height(o, (int32_t)hh);
        if ((long)rows * (long)lh > hh)
            HMI_LOG("widget: roller 的 %d 行 × 行高 %u 超过框高 %ld，只能滚动看全",
                    rows, (unsigned)lh, hh);
    }
    out->obj = o;
    out->type = W_ROLLER;
    (void)hh;
    return 0;
}

/* ── history_table：趋势文件 → 可滚动表格（37 §6.5） ──
 * 读 trends/<group>.bin 最近 last_min 分钟的记录，最新的在最上面，手指上下滚动看更早的。
 *
 * 三件事决定了实现方式：
 *  1. 不用 lv_table：它的行高 = 字高 + 内边距、min_height 也不参与，row_height 只能管到表头
 *     （实测 26 设了没用，行距仍是 16），表头也没法换底色。这里自己排行容器 + 每格 label。
 *  2. 行对象窗口化：900 条记录就是 900 个行容器，板端画不动。只保留「可见行数 + 2」个行对象，
 *     滚动时改起始行、重排 y 并重填文字；内容高度用一个透明 spacer 撑起来，滚动条才有行程。
 *  3. 记录在拆页前读进内存（一份 ~32KB），滚动时不读盘；拆页时 history_table_dispose 释放。 */

/* 按时间从新到旧；环形文件回绕后文件顺序不保证单调，所以读进来先排序 */