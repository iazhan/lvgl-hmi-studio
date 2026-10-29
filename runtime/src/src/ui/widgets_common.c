/*
 * widgets_common.c — 控件建法共用的东西：父对象栈、坐标与颜色、文本样式、可滚动数据区
 *
 * 这里的 place()/apply_text_style()/widget_id() 等是"src/ui 内部接口"（见
 * widgets_internal.h），不是对外的控件 API；对外只有 widgets.h 里那些 ui_* 函数。
 */
#include "widgets_internal.h"
#include "widget_chart.h"
#include "widget_alarm.h"
#include "widget_recipe.h"
#include "core/expr.h"
#include "ui_font.h"
#include "hmi_log.h"
#include "hmi_board.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* 建控件时的父对象：容器里的子控件要挂到容器上，顶层控件挂屏幕 */
static lv_obj_t* s_parent;

lv_obj_t* cur_parent(void)
{
    return s_parent ? s_parent : lv_screen_active();
}

void ui_set_parent(lv_obj_t* parent)
{
    s_parent = parent;
}

/* 容器里的子控件只做布局，不参与绑定与动作。对象随父容器销毁，这里只留一份
 * 记录，用于拆页时释放可能占用的像素缓冲。 */
#define UI_KIDS_MAX 32
static widget_t s_kids[UI_KIDS_MAX];
static int      s_nkids;

void ui_children_reset(void)
{
    s_nkids = 0;
    s_parent = NULL;
}

void ui_children_free_images(void)
{
    for (int i = 0; i < s_nkids; i++) {
        /* 只有图片族才有 img_buf；子控件可能是任何类型，别去读别的族的 union 成员 */
        if (s_kids[i].type != W_IMAGE && s_kids[i].type != W_STATE_IMAGE &&
            s_kids[i].type != W_FRAME_ANIM)
            continue;
        if (s_kids[i].u.img.img_buf) {
            free(s_kids[i].u.img.img_buf);
            s_kids[i].u.img.img_buf = NULL;
        }
    }
    s_nkids = 0;
}

/* ── 颜色 ── */
int ui_color_valid(const char* s)
{
    if (!s || s[0] != '#' || strlen(s) != 7) return 0;
    for (int i = 1; i < 7; i++) {
        char c = s[i];
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
              (c >= 'A' && c <= 'F')))
            return 0;
    }
    return 1;
}

lv_color_t ui_color_hex(const char* s)
{
    unsigned v = 0;

    if (!ui_color_valid(s)) {
        /* 主题令牌（"@bg"）在编辑器导出时就要变成 #RRGGBB；这里出现说明工程没用编辑器
         * 导出过，落黑色比静默取缺省更好查——只报一次，别刷满日志。 */
        static int warned;

        if (s && s[0] == '@' && !warned) {
            warned = 1;
            HMI_LOG("ui: 颜色 %s 是主题令牌，运行时不解析（主题由编辑器导出时烘焙）", s);
        }
        return lv_color_hex(0x000000);
    }
    for (int i = 1; i < 7; i++) {
        char c = s[i];
        unsigned d;
        if (c >= '0' && c <= '9') d = (unsigned)(c - '0');
        else if (c >= 'a' && c <= 'f') d = (unsigned)(c - 'a' + 10);
        else d = (unsigned)(c - 'A' + 10);
        v = (v << 4) | d;
    }
    return lv_color_hex(v);
}

/* ── 通用字段 ── */
const char* widget_id(const hmi_json_t* w)
{
    return hmi_json_str(hmi_json_get(w, "id"), "?");
}

/* x/y/w/h：必须落在屏幕内（37 §8：控件超出屏幕范围是错误） */
int widget_rect(const hmi_json_t* w, long* x, long* y, long* ww, long* hh)
{
    *x = hmi_json_int(hmi_json_get(w, "x"), -1);
    *y = hmi_json_int(hmi_json_get(w, "y"), -1);
    *ww = hmi_json_int(hmi_json_get(w, "w"), -1);
    *hh = hmi_json_int(hmi_json_get(w, "h"), -1);
    if (*x < 0 || *y < 0 || *ww <= 0 || *hh <= 0) return -1;
    if (*x + *ww > HMI_LCD_WIDTH || *y + *hh > HMI_LCD_HEIGHT) return -1;
    return 0;
}

/* def_fs：该类型的契约缺省字号。label / enum_text 是 16，value 是 20（读数比正文大一号），
 * 所以不能写死在共用函数里 —— 缺键时字体会按调用方给的值走，与 spec 的 default 一致。 */
void apply_text_style(lv_obj_t* obj, const hmi_json_t* props, int def_fs)
{
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#E6EDF3");
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), def_fs);
    const char* align = hmi_json_str(hmi_json_get(props, "align"), "left");

    {
        /* 字形（契约 label.font_style）：粗体/斜体是 FreeType 合成样式，不需要第二套字体 */
        const char* style = hmi_json_str(hmi_json_get(props, "font_style"), "normal");
        int st = strcmp(style, "bold") == 0 ? 1 : (strcmp(style, "italic") == 0 ? 2 : 0);

        lv_obj_set_style_text_font(obj, ui_font_ex(fs, st), 0);
    }
    lv_obj_set_style_text_color(obj, ui_color_hex(color), 0);
    if (strcmp(align, "center") == 0)
        lv_obj_set_style_text_align(obj, LV_TEXT_ALIGN_CENTER, 0);
    else if (strcmp(align, "right") == 0)
        lv_obj_set_style_text_align(obj, LV_TEXT_ALIGN_RIGHT, 0);

    /* 字距/行距（契约里只有 label 有这两项；别的控件 props 里没有这个键，
     * hmi_json_int 拿不到就返回 0 ⇒ 不会误设）。0 表示用字体自然间距。 */
    {
        int ls = (int)hmi_json_int(hmi_json_get(props, "letter_spacing"), 0);
        int lns = (int)hmi_json_int(hmi_json_get(props, "line_spacing"), 0);

        if (ls > 0 && ls <= 32) lv_obj_set_style_text_letter_space(obj, (int32_t)ls, 0);
        if (lns > 0 && lns <= 64) lv_obj_set_style_text_line_space(obj, (int32_t)lns, 0);
    }
}

/* 文本垂直对齐：LVGL 的 text_align 只管水平，垂直靠上内边距把内容推下去。
 * 行高从字体取，不拿 font_size 猜（中文行高比字号大）。 */
/*
 * 文本块的实际高度（多行时是 N 行，不是一行）。
 *
 * 为什么不用 lv_obj_get_self_height：label 没实现 GET_SELF_SIZE（只有
 * checkbox/roller/line 那几类响应），拿回来是 0。这里用"最后一个字符所在行 + 行高"
 * 去量 —— 自动换行也算得对，因为 get_letter_pos 走的就是排版后的行。
 *
 * 之前按**单行**行高算 pad：两行文本时"居中"实际落在偏下、"底部"把第二行顶出框外
 * （面板上表现为"选底部直接看不到字"）。
 */
static int text_block_height(lv_obj_t* obj, int lh)
{
    uint32_t len;
    lv_point_t pos;

    if (!lv_obj_check_type(obj, &lv_label_class)) return lh;

    len = (uint32_t)lv_strlen(lv_label_get_text(obj));
    if (len == 0) return lh;

    lv_obj_update_layout(obj);
    lv_label_get_letter_pos(obj, len - 1, &pos);
    return (int)pos.y + lh;
}

void apply_text_valign(lv_obj_t* obj, const hmi_json_t* props, long hh, int def_fs)
{
    const char* va = hmi_json_str(hmi_json_get(props, "valign"), "top");
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), def_fs);
    int lh = lv_font_get_line_height(ui_font(fs));
    int th;
    int avail;
    int pad = 0;

    if (strcmp(va, "middle") != 0 && strcmp(va, "bottom") != 0) {
        lv_obj_set_style_pad_top(obj, 0, 0);
        return;
    }

    th = text_block_height(obj, lh);
    /* 可用高度要用**内容区**，不能用整框高：border_width 与既有 padding 都会吃掉高度。
     * 只按 hh 算的话，带 10px 边框的 55 高标签内容区只剩 35，pad 一加就顶出可视区
     * （"居中"看着像贴底、"底部"直接看不见字）。此时 pad_top 还是 0，所以这里拿到的
     * 就是扣掉边框与既有 padding 后的净高度。 */
    avail = (int)lv_obj_get_content_height(obj);
    if (avail <= 0) avail = (int)hh;

    if (strcmp(va, "middle") == 0) pad = (avail - th) / 2;
    else pad = avail - th;
    if (pad < 0) pad = 0;     /* 文本比内容区高时不往上顶，让它自然溢出 */
    lv_obj_set_style_pad_top(obj, pad, 0);
}

/* ── label ── */
char ui_asset_root[256];

const project_t* ui_proj_ctx;

/*
 * 把 SC_COMMON 里的外观属性应用到控件上：**有则覆盖，无则不动**。
 *
 * 为什么需要它：label / input / combobox / roller 这几类原本要么硬编码一套颜色、
 * 要么强制透明，编辑器里设的底色/描边只在 C# 画布上看得见，位图里完全没生效 ——
 * 用户看到的"下拉框看不到框、滚轮是一块浅色、标签底色和字色一样"都出自这里。
 *
 * 不做成"一律设成 props 的值"：没配色的控件要继续吃各自的默认外观，
 * 否则会把本来正常的控件画平。
 */
static void box_style(lv_obj_t* obj, const hmi_json_t* props, int with_bg,
                      int with_indicator)
{
    const char* bg;
    const char* border;
    long bw;
    long radius;

    if (!obj || !props) return;

    bg = hmi_json_str(hmi_json_get(props, "bg_color"), NULL);
    if (with_bg && bg && ui_color_valid(bg)) {
        lv_obj_set_style_bg_color(obj, ui_color_hex(bg), 0);
        lv_obj_set_style_bg_opa(obj, LV_OPA_COVER, 0);
    }

    border = hmi_json_str(hmi_json_get(props, "border_color"), NULL);
    if (border && ui_color_valid(border))
        lv_obj_set_style_border_color(obj, ui_color_hex(border), 0);

    bw = hmi_json_int(hmi_json_get(props, "border_width"), -1);
    if (bw >= 0) lv_obj_set_style_border_width(obj, (int32_t)bw, 0);

    radius = hmi_json_int(hmi_json_get(props, "radius"), -1);
    if (radius >= 0) {
        lv_obj_set_style_radius(obj, (int32_t)radius, 0);
        /* 复合控件里"看得见的那一条"是 INDICATOR 段：滑块与进度条的填充段、
         * 开关的通态段、复选框的方框。只刷 MAIN 的话，圆角改了而填充段两端还是
         * 主题给的圆头，用户看到的就是"改了圆角、那条一点没变"。 */
        if (with_indicator) lv_obj_set_style_radius(obj, (int32_t)radius, LV_PART_INDICATOR);
    }
}

void apply_box_style(lv_obj_t* obj, const hmi_json_t* props)
{
    box_style(obj, props, 1, 0);
}

/* 有「看得见的 INDICATOR 段」的复合控件（见 box_style 里的说明）。 */
static int has_indicator_part(int type)
{
    return type == W_SWITCH || type == W_BAR || type == W_SLIDER || type == W_CHECKBOX;
}

/*
 * 建页收口：把 SC_COMMON 里的外观键一次应用到控件根对象上。
 *
 * 为什么放在收口而不是每个 build_* 里各写一遍：export 对**每个**类型都写
 * bg_color / border_color / border_width / radius / opacity，但此前只有 label /
 * button / 图元 / 曲线四类真的读 —— 用户在下拉框、表格、滚轮、指示器上改颜色，
 * 面板与画布（同一份运行时位图）都不动。逐类补叫二十多处重复代码，漏一个就是
 * 一类"设了没用"；收口一处，新增控件自动继承。
 *
 * 三处例外：
 *   - 图元（shape 族）：fill_color / border_color / radius 是**几何**的一部分，由
 *     ui_figure 自己读；这里再刷一遍根对象底色会在圆、多边形背后多出一个矩形。
 *   - lamp（lv_led）：LED 的"底色"就是灯珠颜色（props.color），不是背景。
 *   - switch：bg_color 只当 color_off 的兜底（见 build_switch）；它有自己的一对
 *     通断颜色，这里再刷 MAIN 会把面板上明确配的「断开色」盖掉。
 */
lv_obj_t* widget_paint_obj(const widget_t* w)
{
    if (!w) return NULL;
    return (w->type == W_LAMP && w->aux) ? w->aux : w->obj;
}

void apply_common_style(widget_t* w, const hmi_json_t* props)
{
    long opa, vis, enabled;

    if (!w || !w->obj || !props) return;

    /* 管道也是"自己画几何"的控件：对象本身必须完全透明。
     * 给它上盒子样式的话，bg_color 会把**整个控件矩形**刷成背景色，管壁/通道反而被盖住
     * （面板上「背景色」这一行对管道没有意义，导出的键直接忽略）。 */
    if (w->type != W_SHAPE && w->type != W_LAMP && w->type != W_PIPE)
        box_style(w->obj, props, w->type != W_SWITCH, has_indicator_part(w->type));

    /* 不透明度（契约 0..255，缺省 255 = 不透明）。只在真的半透明时才设样式：
     * LVGL 对 opa < 255 的对象要走离屏图层，全屏控件上这个代价不该白付。 */
    opa = hmi_json_int(hmi_json_get(props, "opacity"), 255);
    if (opa < 0) opa = 0;
    if (opa > 255) opa = 255;
    if (opa < 255) lv_obj_set_style_opa(w->obj, (lv_opa_t)opa, 0);

    /* 隐藏（契约 visible，缺省 true）：建页就藏起来，不是建完再藏，
     * 否则首帧会闪一下。 */
    vis = hmi_json_bool(hmi_json_get(props, "visible"), 1);
    if (!vis) lv_obj_add_flag(w->obj, LV_OBJ_FLAG_HIDDEN);

    /* enabled 是静态初值；bind.enabled 与 states.enabled 会在后续阶段覆盖它。 */
    enabled = hmi_json_bool(hmi_json_get(props, "enabled"), 1);
    if (!enabled) {
        lv_obj_add_state(w->obj, LV_STATE_DISABLED);
        lv_obj_clear_flag(w->obj, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_opa(w->obj, LV_OPA_50, 0);
    }
}

void ui_set_project(const project_t* p)
{
    ui_proj_ctx = p;
}

void ui_set_asset_root(const char* root)
{
    snprintf(ui_asset_root, sizeof(ui_asset_root), "%s", root ? root : "");
}

/* 图片：src 指向 assets/img 下的 .bin；坏图必须报错（不能拿错数据去画） */
void place(lv_obj_t* o, long x, long y, long ww, long hh)
{
    lv_obj_set_pos(o, (int32_t)x, (int32_t)y);
    lv_obj_set_size(o, (int32_t)ww, (int32_t)hh);
    lv_obj_set_style_pad_all(o, 0, 0);
    lv_obj_set_style_border_width(o, 0, 0);
}

/* ui_figure.c 里建图元对象要用同样的父对象与摆放规则 */
lv_obj_t* ui_cur_parent(void)
{
    return cur_parent();
}

void ui_place(lv_obj_t* o, long x, long y, long ww, long hh)
{
    place(o, x, y, ww, hh);
}

/* 滚动条可拖：LVGL 9 的手势只认"拖内容"（见 lv_indev_find_scroll_obj），那条 6px 的
 * 滚动条纯属装饰——按在上面也只是 1:1 拖内容，表格几十行时没法一下拉到末尾。
 * 在滚动条槽上盖一层透明接收层（不可滚动、不把滚动链传给父对象），按住它拖动时按
 * "手指位移 → 内容位移"的比例滚。它比 6px 宽一些，手指才好按住。 */
#define SB_GRAB_W 12

static struct {
    lv_obj_t* handle;
    lv_obj_t* view;
    int start_y;
    int start_top;
    int ratio;                 /* 手指 1px ≈ 内容 ratio px */
} s_sb;

static void sb_drag_press(lv_event_t* e)
{
    lv_obj_t* view = (lv_obj_t*)lv_event_get_user_data(e);
    lv_indev_t* indev = lv_indev_active();
    lv_point_t p;
    int ch, range, bar, ratio;

    if (!view || !indev) return;
    ch = (int)lv_obj_get_content_height(view);
    range = (int)(lv_obj_get_scroll_top(view) + lv_obj_get_scroll_bottom(view));
    if (ch <= 0 || range <= 0) return;
    bar = ch * ch / (ch + range);            /* 与 LVGL 画出来的滑块同量级 */
    if (bar < 24) bar = 24;
    if (bar > ch) bar = ch;
    ratio = (ch > bar) ? range / (ch - bar) : 1;
    if (ratio < 1) ratio = 1;
    lv_indev_get_point(indev, &p);
    s_sb.handle = lv_event_get_target_obj(e);
    s_sb.view = view;
    s_sb.start_y = (int)p.y;
    s_sb.start_top = -(int)lv_obj_get_scroll_y(view);
    s_sb.ratio = ratio;
    /* 抓到的反馈：滚动条提亮，手指下面能看出这一下是拖条而不是拖内容 */
    lv_obj_set_style_bg_color(view, ui_color_hex("#6B7F94"), LV_PART_SCROLLBAR);
}

static void sb_drag_move(lv_event_t* e)
{
    lv_indev_t* indev = lv_indev_active();
    lv_point_t p;
    int range, top;

    LV_UNUSED(e);
    if (!s_sb.view || !indev) return;
    range = (int)(lv_obj_get_scroll_top(s_sb.view) + lv_obj_get_scroll_bottom(s_sb.view));
    lv_indev_get_point(indev, &p);
    top = s_sb.start_top + ((int)p.y - s_sb.start_y) * s_sb.ratio;
    if (top < 0) top = 0;
    if (top > range) top = range;
    lv_obj_scroll_to_y(s_sb.view, top, LV_ANIM_OFF);
}

static void sb_drag_end(lv_event_t* e)
{
    LV_UNUSED(e);
    if (s_sb.view && lv_obj_is_valid(s_sb.view))
        lv_obj_set_style_bg_color(s_sb.view, ui_color_hex("#4A5A6A"), LV_PART_SCROLLBAR);
    s_sb.handle = NULL;
    s_sb.view = NULL;
}

static void sb_handle_add(lv_obj_t* o)
{
    lv_obj_t* par = lv_obj_get_parent(o);
    lv_obj_t* hd;

    if (!par) return;
    /* 建页阶段属性和坐标都还没结算，取 o 的位置/尺寸前先把布局落一遍 */
    lv_obj_update_layout(o);
    hd = lv_obj_create(par);
    if (!hd) return;
    lv_obj_set_pos(hd, lv_obj_get_x(o) + lv_obj_get_width(o) - SB_GRAB_W,
                   lv_obj_get_y(o));
    lv_obj_set_size(hd, SB_GRAB_W, lv_obj_get_height(o));
    lv_obj_remove_flag(hd, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_clear_flag(hd, LV_OBJ_FLAG_SCROLL_CHAIN_HOR);
    lv_obj_clear_flag(hd, LV_OBJ_FLAG_SCROLL_CHAIN_VER);
    lv_obj_set_style_bg_opa(hd, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(hd, 0, 0);
    lv_obj_set_style_pad_all(hd, 0, 0);
    lv_obj_add_event_cb(hd, sb_drag_press, LV_EVENT_PRESSED, o);
    lv_obj_add_event_cb(hd, sb_drag_move, LV_EVENT_PRESSING, NULL);
    lv_obj_add_event_cb(hd, sb_drag_end, LV_EVENT_RELEASED, NULL);
    lv_obj_add_event_cb(hd, sb_drag_end, LV_EVENT_PRESS_LOST, NULL);
    lv_obj_add_event_cb(hd, sb_drag_end, LV_EVENT_DELETE, NULL);
    lv_obj_move_foreground(hd);
}

/* 可滚动数据区的统一外观：竖向滚动 + 6px 圆角滚动条（可拖，见上）。
 * history_table / recipe_table / alarm_list 共用这一份，避免三处各写一套颜色与宽度。 */
void ui_scroll_area(lv_obj_t* o, int scrollbar)
{
    if (!o) return;
    lv_obj_add_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_scroll_dir(o, LV_DIR_VER);
    lv_obj_set_scrollbar_mode(o, scrollbar ? LV_SCROLLBAR_MODE_ON : LV_SCROLLBAR_MODE_OFF);
    lv_obj_set_style_width(o, 6, LV_PART_SCROLLBAR);
    lv_obj_set_style_bg_color(o, ui_color_hex("#4A5A6A"), LV_PART_SCROLLBAR);
    lv_obj_set_style_bg_opa(o, LV_OPA_COVER, LV_PART_SCROLLBAR);
    lv_obj_set_style_radius(o, 3, LV_PART_SCROLLBAR);
    lv_obj_set_style_pad_right(o, 2, LV_PART_SCROLLBAR);
    if (scrollbar) sb_handle_add(o);
}

/* 趋势数据根目录（其下 trends/）：main 启动时设置一次；曲线与历史表都从这里取 */
static const char* s_trend_dir = "/data/chenge";

void ui_set_trend_dir(const char* dir)
{
    if (dir && *dir) s_trend_dir = dir;
}

const char* ui_trend_dir(void)
{
    return s_trend_dir;
}

/* 拆页时释放一个控件占用的堆内存与定时器。
 * 族字段现在在 union 里，必须按 type 分派——对任何控件都去读 u.curve 会读到别的族的数据。 */
void widget_dispose(widget_t* w)
{
    if (!w) return;

    switch (w->type) {
    case W_REALTIME_CURVE:
    case W_HISTORY_CURVE:
    case W_XY_CURVE:
        for (int k = 0; k < 4; k++) {
            free(w->u.curve.hist[k]);
            w->u.curve.hist[k] = NULL;
        }
        for (int k = 0; k < w->u.curve.nseries; k++)
            if (w->u.curve.curve_has_expr[k]) expr_free(&w->u.curve.curve_expr[k]);
        w->u.curve.nseries = 0;
        if (w->u.curve.ttimer) {
            lv_timer_delete(w->u.curve.ttimer);
            w->u.curve.ttimer = NULL;
        }
        if (w->u.curve.curve_timer) {
            lv_timer_delete(w->u.curve.curve_timer);
            w->u.curve.curve_timer = NULL;
        }
        break;
    case W_HISTORY_TABLE:
        history_table_dispose(w);
        break;
    case W_ALARM_BAR:
    case W_ALARM_LIST:
        alarm_widget_dispose(w);
        break;
    case W_RECIPE_TABLE:
        recipe_widget_dispose(w);
        break;
    case W_CHART:
        chart_dispose(w);
        break;
    case W_SHAPE:
        free(w->u.shape.poly_pts);          /* 折线/多边形端点，lv_line 只引用不拷贝 */
        w->u.shape.poly_pts = NULL;
        break;
    case W_GAUGE:
        free(w->u.gauge.tick_pts);          /* 刻度端点被 lv_line 引用 */
        w->u.gauge.tick_pts = NULL;
        break;
    case W_IMAGE:
    case W_STATE_IMAGE:
    case W_FRAME_ANIM:
        for (int k = 0; k < w->u.img.nstates; k++) free(w->u.img.states[k].buf);
        free(w->u.img.states);
        w->u.img.states = NULL;
        w->u.img.nstates = 0;
        free(w->u.img.img_buf);
        w->u.img.img_buf = NULL;
        if (w->u.img.anim_timer) {
            lv_timer_delete(w->u.img.anim_timer);
            w->u.img.anim_timer = NULL;
        }
        break;
    case W_PIPE:
        if (w->u.pipe.flow_timer) {
            lv_timer_delete(w->u.pipe.flow_timer);
            w->u.pipe.flow_timer = NULL;
        }
        break;
    case W_MESSAGE:
        if (w->u.msg.msg_timer) {
            lv_timer_delete(w->u.msg.msg_timer);
            w->u.msg.msg_timer = NULL;
        }
        break;
    default:
        break;
    }

    /* states 是通用机制（任何控件都能配），不属于某一族，留在核心 */
    if (w->wstates) {
        for (int k = 0; k < w->nwstates; k++)
            if (w->wstates[k].has_expr) expr_free(&w->wstates[k].expr);
        free(w->wstates);
        w->wstates = NULL;
        w->nwstates = 0;
    }
}
