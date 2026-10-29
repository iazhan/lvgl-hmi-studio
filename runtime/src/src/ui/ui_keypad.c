/*
 * ui_keypad.c — 通用键盘（ADR-0010）：数字盘 + ASCII 文本盘
 *
 * 自建按键而不是 lv_keyboard：后者的 OK/退格是 LV_SYMBOL 字形，中文字体子集里没有，
 * 会显示成方块。这里只用 ASCII 标签，跟字体无关。
 *
 * 数字盘 360×300（底部居中）：
 *   7 8 9 DEL / 4 5 6 C / 1 2 3 X / 0 . - OK
 * 文本盘 700×320（底部居中，10 列）：
 *   0 1 2 3 4 5 6 7 8 9
 *   Q W E R T Y U I O P
 *   A S D F G H J K L .
 *   Z X C V B N M - _ DEL
 *   [清空] [空格] [取消] [OK]
 */
#include "ui_keypad.h"
#include "ui_font.h"
#include "hmi_board.h"
#include "hmi_log.h"
#include "core/i18n.h"
#include "widgets.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define KP_TXT_MAX 32

/* 数字盘 */
#define NUM_W 360
#define NUM_H 300
/* 文本盘 */
#define TXT_W 700
#define TXT_H 320

#define KP_PAD 8
#define KP_TITLE_H 16
#define KP_DISP_H 44
/* 提示行按 10 号 FreeType 的行高留（含汉字下沿），12 会把字切掉一半 */
#define KP_HINT_H 20
#define KP_GAP 4

static lv_obj_t*    s_screen;
static lv_obj_t*    s_mask;
static lv_obj_t*    s_box;
/* 内联键盘（keyboard 控件）：s_mask 为空、s_box 挂在控件容器里。
 * 键盘状态是模块级单例，所以一页只允许一个内联键盘。 */
static int          s_inline;
static lv_obj_t*    s_title;
static lv_obj_t*    s_disp;         /* 显示行容器 */
static lv_obj_t*    s_disp_lbl;     /* 显示行里的 label（文本写它，不能写容器） */
static lv_obj_t*    s_hint;
static keypad_mode_t s_mode;
static keypad_req_t s_req;
static char         s_txt[KP_TXT_MAX];
static int          s_committed;      /* 本次输入是否已走确认路径（挡住 on_close 重复写） */

void ui_keypad_set_screen(lv_obj_t* screen)
{
    s_screen = screen;
}

void ui_keypad_close(void)
{
    if (s_mask) lv_obj_add_flag(s_mask, LV_OBJ_FLAG_HIDDEN);

    /* commit=blur：这次输入结束就写回（确认路径已经写过一次，靠 s_committed 挡住） */
    if (!s_committed && s_txt[0]) {
        if (s_req.on_close_text) {
            s_req.on_close_text(s_req.ud, s_txt);
        } else if (s_req.on_close_num) {
            double v = strtod(s_txt, NULL);

            if (s_req.has_min && v < s_req.min) v = s_req.min;
            if (s_req.has_max && v > s_req.max) v = s_req.max;
            s_req.on_close_num(s_req.ud, v);
        }
    }
    s_committed = 0;
    /* 内联键盘是常驻控件：关掉只是"这次输入结束"，回调要留着给下一次用 */
    if (!s_inline) memset(&s_req, 0, sizeof(s_req));
}

void ui_keypad_reset(void)
{
    /* 对象是 screen 的子对象，随 screen 一起删；这里只丢指针 */
    s_mask = NULL;
    s_box = NULL;
    s_title = NULL;
    s_disp = NULL;
    s_disp_lbl = NULL;
    s_hint = NULL;
    s_screen = NULL;
    s_inline = 0;
    memset(&s_req, 0, sizeof(s_req));
}

/* ── 校验 ── */

static int num_valid(const char* t)
{
    int dots = 0;

    for (const char* p = t; *p; p++) {
        if (*p >= '0' && *p <= '9') continue;
        if (*p == '.') {
            if (++dots > 1) return 0;
            continue;
        }
        if (*p == '-' && p == t) continue;
        return 0;
    }
    return 1;
}

/* 文本只收 A-Z 0-9 . - _（不含空格与中文，ADR-0010 决定 6） */
static int txt_valid(const char* t)
{
    for (const char* p = t; *p; p++) {
        char c = *p;

        if (c >= 'A' && c <= 'Z') continue;
        if (c >= '0' && c <= '9') continue;
        if (c == '.' || c == '-' || c == '_') continue;
        return 0;
    }
    return 1;
}

static void kp_show(void)
{
    if (!s_disp_lbl) return;
    lv_label_set_text(s_disp_lbl, s_txt[0] ? s_txt : (s_mode == KEYPAD_NUM ? "0" : ""));
}

/* ── 提交 ── */

static void kp_commit(void)
{
    if (s_mode == KEYPAD_TEXT) {
        void (*cb)(void*, const char*) = s_req.on_commit_text;
        void* ud = s_req.ud;

        if (s_txt[0] == '\0') {
            lv_label_set_text(s_hint, i18n_rt("kp.empty"));
            return;
        }
        /* 长度不够就地拦：放行到回调再退回来，用户会先被要求"再输一次"才看到原因 */
        if (s_req.min_len > 0 && (int)strlen(s_txt) < s_req.min_len) {
            char hb[64];

            HMI_LOG("keypad: 输入 %d 位不足下限 %d，未提交", (int)strlen(s_txt), s_req.min_len);
            snprintf(hb, sizeof(hb), i18n_rt("kp.too_short"), s_req.min_len);
            lv_label_set_text(s_hint, hb);
            return;
        }
        s_committed = 1;
        ui_keypad_close();
        if (cb) cb(ud, s_txt);
        return;
    }
    {
        char* end = NULL;
        double v;
        void (*cb)(void*, double) = s_req.on_commit_num;
        void* ud = s_req.ud;

        if (s_txt[0] == '\0' || strcmp(s_txt, "-") == 0 || strcmp(s_txt, ".") == 0) {
            lv_label_set_text(s_hint, i18n_rt("kp.need_num"));
            return;
        }
        v = strtod(s_txt, &end);
        if (!end || *end != '\0' || !isfinite(v)) {
            lv_label_set_text(s_hint, i18n_rt("kp.bad_num"));
            return;
        }
        if (s_req.has_min && v < s_req.min) v = s_req.min;
        if (s_req.has_max && v > s_req.max) v = s_req.max;
        s_committed = 1;
        ui_keypad_close();
        if (cb) cb(ud, v);
    }
}

static void kp_key_cb(lv_event_t* e)
{
    const char* k = (const char*)lv_event_get_user_data(e);
    size_t n = strlen(s_txt);

    lv_label_set_text(s_hint, "");
    if (strcmp(k, "OK") == 0) { kp_commit(); return; }
    if (strcmp(k, "X") == 0) { ui_keypad_close(); return; }
    if (strcmp(k, "DEL") == 0) {
        /* 按 UTF-8 字符删：从原始名字（可能含中文）退格时不能只砍一个字节 */
        if (n > 0) {
            n--;
            while (n > 0 && ((unsigned char)s_txt[n] & 0xC0) == 0x80) n--;
            s_txt[n] = '\0';
        }
        kp_show();
        return;
    }
    if (strcmp(k, "C") == 0) {
        s_txt[0] = '\0';
        kp_show();
        return;
    }
    if (strcmp(k, "SP") == 0) k = " ";
    if (n + 1 >= sizeof(s_txt)) {
        lv_label_set_text(s_hint, i18n_rt("kp.too_long"));
        return;
    }
    if (s_mode == KEYPAD_TEXT && s_req.max_len > 0 && (int)n >= s_req.max_len) {
        /* 把"已用/上限"打出来：max_len 是字节数，中文占 3 字节，光说"太长"看不出差多少 */
        char hb[64];

        snprintf(hb, sizeof(hb), "%s %d/%d", i18n_rt("kp.too_long"), (int)n, s_req.max_len);
        lv_label_set_text(s_hint, hb);
        return;
    }
    if (s_mode == KEYPAD_TEXT) {
        char one[2] = { k[0], '\0' };

        /* 只看新输入的字符：原名里可能带中文，整串校验会让改名直接不可用 */
        if (!txt_valid(one)) {
            lv_label_set_text(s_hint, i18n_rt("kp.ascii_only"));
            return;
        }
        s_txt[n] = k[0];
        s_txt[n + 1] = '\0';
        kp_show();
        return;
    }
    s_txt[n] = k[0];
    s_txt[n + 1] = '\0';
    if (!num_valid(s_txt)) {
        s_txt[n] = '\0';                      /* 非法字符直接拒收 */
        lv_label_set_text(s_hint, i18n_rt("kp.bad_char"));
        return;
    }
    kp_show();
}

static void kp_mask_cb(lv_event_t* e)
{
    (void)e;
    ui_keypad_close();
}

/* ── 构建 ── */

static lv_obj_t* kp_key(lv_obj_t* parent, int x, int y, int w, int h, const char* text,
                        const char* bg)
{
    lv_obj_t* b = lv_obj_create(parent);

    if (!b) return NULL;
    lv_obj_set_pos(b, (int32_t)x, (int32_t)y);
    lv_obj_set_size(b, (int32_t)w, (int32_t)h);
    lv_obj_remove_flag(b, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_color(b, ui_color_hex(bg), 0);
    lv_obj_set_style_bg_opa(b, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(b, 0, 0);
    lv_obj_set_style_radius(b, 6, 0);
    lv_obj_set_style_pad_all(b, 0, 0);
    {
        lv_obj_t* l = lv_label_create(b);

        if (l) {
            lv_label_set_text(l, text);
            lv_obj_set_style_text_font(l, ui_font(16), 0);
            lv_obj_set_style_text_color(l, ui_color_hex("#FFFFFF"), 0);
            lv_obj_set_style_bg_opa(l, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(l, 0, 0);
            lv_obj_center(l);
        }
    }
    lv_obj_add_event_cb(b, kp_key_cb, LV_EVENT_CLICKED, (void*)text);
    return b;
}

/* parent 为空 = 模态键盘（铺一层遮罩、贴在屏底）；非空 = 内联键盘（挂在控件里） */
static int kp_build_frame(lv_obj_t* parent, int w, int h)
{
    if (parent) {
        s_inline = 1;
        s_mask = NULL;
        s_box = lv_obj_create(parent);
        if (!s_box) return -1;
        lv_obj_set_pos(s_box, 0, 0);
        lv_obj_set_size(s_box, (int32_t)w, (int32_t)h);
    } else {
        s_inline = 0;
        if (!s_screen) return -1;

        s_mask = lv_obj_create(s_screen);
        if (!s_mask) return -1;
        lv_obj_set_pos(s_mask, 0, 0);
        lv_obj_set_size(s_mask, HMI_LCD_WIDTH, HMI_LCD_HEIGHT);
        lv_obj_remove_flag(s_mask, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_set_style_bg_color(s_mask, ui_color_hex("#000000"), 0);
        lv_obj_set_style_bg_opa(s_mask, LV_OPA_40, 0);
        lv_obj_set_style_border_width(s_mask, 0, 0);
        lv_obj_set_style_radius(s_mask, 0, 0);
        lv_obj_set_style_pad_all(s_mask, 0, 0);
        lv_obj_add_event_cb(s_mask, kp_mask_cb, LV_EVENT_CLICKED, NULL);

        s_box = lv_obj_create(s_mask);
        if (!s_box) return -1;
        lv_obj_set_size(s_box, (int32_t)w, (int32_t)h);
        lv_obj_align(s_box, LV_ALIGN_BOTTOM_MID, 0, 0);
    }
    lv_obj_remove_flag(s_box, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_color(s_box, ui_color_hex("#1B2530"), 0);
    lv_obj_set_style_bg_opa(s_box, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(s_box, 1, 0);
    lv_obj_set_style_border_color(s_box, ui_color_hex("#3A4A5A"), 0);
    lv_obj_set_style_radius(s_box, 8, 0);
    lv_obj_set_style_pad_all(s_box, KP_PAD, 0);

    s_title = lv_label_create(s_box);
    if (s_title) {
        lv_label_set_text(s_title, "");
        lv_obj_set_pos(s_title, 0, 0);
        lv_obj_set_size(s_title, (int32_t)(w - 2 * KP_PAD), KP_TITLE_H);
        lv_obj_set_style_text_font(s_title, ui_font(12), 0);
        lv_obj_set_style_text_color(s_title, ui_color_hex("#9AA7B5"), 0);
        lv_obj_set_style_bg_opa(s_title, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(s_title, 0, 0);
        lv_obj_set_style_pad_all(s_title, 0, 0);
    }

    s_disp = lv_obj_create(s_box);
    if (!s_disp) return -1;
    lv_obj_set_pos(s_disp, 0, KP_TITLE_H);
    lv_obj_set_size(s_disp, (int32_t)(w - 2 * KP_PAD), KP_DISP_H);
    lv_obj_remove_flag(s_disp, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_color(s_disp, ui_color_hex("#0E1620"), 0);
    lv_obj_set_style_bg_opa(s_disp, LV_OPA_COVER, 0);
    lv_obj_set_style_border_color(s_disp, ui_color_hex("#2F70D0"), 0);
    lv_obj_set_style_border_width(s_disp, 1, 0);
    lv_obj_set_style_radius(s_disp, 6, 0);
    lv_obj_set_style_pad_all(s_disp, 0, 0);
    {
        lv_obj_t* l = lv_label_create(s_disp);

        s_disp_lbl = l;
        if (l) {
            lv_label_set_text(l, "0");
            lv_label_set_long_mode(l, LV_LABEL_LONG_CLIP);
            lv_obj_set_style_text_font(l, ui_font(24), 0);
            lv_obj_set_style_text_color(l, ui_color_hex("#E6EDF3"), 0);
            lv_obj_set_style_bg_opa(l, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(l, 0, 0);
            lv_obj_set_style_pad_all(l, 0, 0);
            lv_obj_align(l, LV_ALIGN_RIGHT_MID, -8, 0);
        }
    }

    s_hint = lv_label_create(s_box);
    if (s_hint) {
        lv_label_set_text(s_hint, "");
        lv_obj_set_pos(s_hint, 0, KP_TITLE_H + KP_DISP_H + 2);
        lv_obj_set_size(s_hint, (int32_t)(w - 2 * KP_PAD), KP_HINT_H);
        lv_obj_set_style_text_font(s_hint, ui_font(10), 0);
        lv_obj_set_style_text_color(s_hint, ui_color_hex("#E05C5C"), 0);
        lv_obj_set_style_bg_opa(s_hint, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(s_hint, 0, 0);
        lv_obj_set_style_pad_all(s_hint, 0, 0);
    }
    return 0;
}

static int kp_build_num(lv_obj_t* parent)
{
    static const char* const keys[4][4] = {
        { "7", "8", "9", "DEL" },
        { "4", "5", "6", "C" },
        { "1", "2", "3", "X" },
        { "0", ".", "-", "OK" },
    };
    int bw = (NUM_W - 2 * KP_PAD - 3 * KP_GAP) / 4;
    int bh = 46;
    int by = KP_TITLE_H + KP_DISP_H + KP_HINT_H + 6;

    if (kp_build_frame(parent, NUM_W, NUM_H) != 0) return -1;
    for (int r = 0; r < 4; r++) {
        for (int c = 0; c < 4; c++) {
            const char* k = keys[r][c];
            const char* bg = "#2F70D0";

            if (strcmp(k, "OK") == 0) bg = "#38C172";
            else if (strcmp(k, "X") == 0) bg = "#5A6B7A";
            else if (strcmp(k, "DEL") == 0 || strcmp(k, "C") == 0) bg = "#3A4A5A";
            (void)kp_key(s_box, c * (bw + KP_GAP), by + r * (bh + KP_GAP), bw, bh, k, bg);
        }
    }
    return 0;
}

static int kp_build_text(lv_obj_t* parent)
{
    static const char* const rows[4][10] = {
        { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" },
        { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" },
        { "A", "S", "D", "F", "G", "H", "J", "K", "L", "." },
        { "Z", "X", "C", "V", "B", "N", "M", "-", "_", "DEL" },
    };
    int bw = (TXT_W - 2 * KP_PAD - 9 * KP_GAP) / 10;
    int bh = 40;
    int by = KP_TITLE_H + KP_DISP_H + KP_HINT_H + 6;
    int aw = (TXT_W - 2 * KP_PAD - 3 * KP_GAP) / 4;

    if (kp_build_frame(parent, TXT_W, TXT_H) != 0) return -1;
    for (int r = 0; r < 4; r++)
        for (int c = 0; c < 10; c++) {
            const char* k = rows[r][c];
            const char* bg = (strcmp(k, "DEL") == 0) ? "#3A4A5A" : "#2F70D0";

            (void)kp_key(s_box, c * (bw + KP_GAP), by + r * (bh + KP_GAP), bw, bh, k, bg);
        }
    /* 动作行：清空 / 空格 / 取消 / 提交 */
    {
        int ay = by + 4 * (bh + KP_GAP);

        (void)kp_key(s_box, 0 * (aw + KP_GAP), ay, aw, bh, "C", "#3A4A5A");
        (void)kp_key(s_box, 1 * (aw + KP_GAP), ay, aw, bh, "SP", "#5A6B7A");
        (void)kp_key(s_box, 2 * (aw + KP_GAP), ay, aw, bh, "X", "#5A6B7A");
        (void)kp_key(s_box, 3 * (aw + KP_GAP), ay, aw, bh, "OK", "#38C172");
    }
    return 0;
}

/* 取一次请求：模式、初值、限制都存进单例，随后所有按键都作用于它 */
static void kp_apply(const keypad_req_t* req)
{
    s_mode = req->mode;
    s_req = *req;
    s_committed = 0;              /* 每次打开都是新的"未提交"输入 */
    if (s_req.decimals < 0) s_req.decimals = 0;
    if (s_req.decimals > 6) s_req.decimals = 6;
    if (s_req.max_len < 1) s_req.max_len = 16;
    if (s_req.max_len > 24) s_req.max_len = 24;
    if (s_title) lv_label_set_text(s_title, req->title ? req->title : "");
    if (s_hint) lv_label_set_text(s_hint, req->hint ? req->hint : "");
    s_req.hint = NULL;                     /* 提示文字由调用方栈上拼出，用完即弃 */
    if (s_mode == KEYPAD_TEXT) {
        snprintf(s_txt, sizeof(s_txt), "%s", req->text ? req->text : "");
    } else {
        snprintf(s_txt, sizeof(s_txt), "%.*f", s_req.decimals, s_req.value);
    }
    kp_show();
}

int ui_keypad_open(const keypad_req_t* req)
{
    int rc;

    if (!req || !s_screen) return -1;
    if (s_box && s_mode != req->mode) {          /* 换模式要重建布局 */
        if (s_mask) lv_obj_delete(s_mask);
        s_mask = NULL;
        s_box = NULL;
        s_disp_lbl = NULL;
    }
    if (!s_mask) {
        rc = (req->mode == KEYPAD_TEXT) ? kp_build_text(NULL) : kp_build_num(NULL);
        if (rc != 0) {
            s_mask = NULL;
            return -1;
        }
    }
    kp_apply(req);
    lv_obj_remove_flag(s_mask, LV_OBJ_FLAG_HIDDEN);
    lv_obj_move_foreground(s_mask);
    return 0;
}

/* 内联键盘（keyboard 控件）：同一套键位建在 parent 里。
 * 与模态版的差别只有三处：没有遮罩、不贴屏底、关掉时不丢回调（下一次输入还要用）。 */
int ui_keypad_open_inline(lv_obj_t* parent, const keypad_req_t* req)
{
    int rc;

    if (!req || !parent) return -1;
    if (s_box) return -1;                        /* 键盘状态是单例：一页只允许一个 */
    rc = (req->mode == KEYPAD_TEXT) ? kp_build_text(parent) : kp_build_num(parent);
    if (rc != 0) {
        s_mask = NULL;
        s_box = NULL;
        return -1;
    }
    kp_apply(req);
    return 0;
}
