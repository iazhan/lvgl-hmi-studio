/*
 * ui_page_input.c — 控件交互回调与提示条：输入框提交、写回、下拉选择、toast
 */
#include "ui_page.h"
#include "ui_page_internal.h"
#include "comm/modbus.h"
#include "script/lua_rt.h"
#include "core/tagdb.h"
#include "core/tagwrite.h"
#include "core/alarm.h"
#include "core/recipe.h"
#include "data/savecfg.h"
#include "hmi_usb.h"          /* 配方导入导出要挂 U 盘（hmi_usb_mount/umount） */
#include "ui_font.h"
#include "ui_keypad.h"
#include "ui_login.h"
#include "core/audit.h"
#include "core/auth.h"
#include "core/i18n.h"
#include "widget_alarm.h"
#include "widget_recipe.h"
#include "hmi_board.h"
#include "hmi_json.h"
#include "hmi_log.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* 提示框：2 秒后自动消失。挂在活动屏上，随页一起被删。
 * 同一时刻只留一条：新提示顶掉旧的（旧条的定时器被删掉后，条本身没人收，会永远留在屏上）。 */
static lv_timer_t* s_toast_timer;
static lv_obj_t*   s_toast_box;

static void toast_timer_cb(lv_timer_t* t)
{
    lv_obj_t* box = (lv_obj_t*)lv_timer_get_user_data(t);

    if (box && lv_obj_is_valid(box)) lv_obj_delete(box);
    lv_timer_delete(t);
    if (s_toast_timer == t) s_toast_timer = NULL;
    s_toast_box = NULL;
}

/* 收掉当前提示条（拆页时也走这里，定时器属于本文件） */
void toast_reset(void)
{
    if (s_toast_timer) {
        lv_timer_delete(s_toast_timer);
        s_toast_timer = NULL;
    }
    if (s_toast_box && lv_obj_is_valid(s_toast_box)) lv_obj_delete(s_toast_box);
    s_toast_box = NULL;
}

void toast_show(const char* text)   /* 提示条：控件也能用（配方表拒绝时提示） */
{
    lv_obj_t* box;

    /* 上一条还没到期就先收掉：定时器只有一个，留着旧盒子等于让它永不消失 */
    if (s_toast_box) {
        const char* e = getenv("HMI_RT_TRACE");

        if (e && *e == '1') HMI_LOG("toast: 新提示顶掉未消失的旧提示");
        toast_reset();
    }
    box = lv_obj_create(lv_screen_active());
    if (!box) return;
    lv_obj_set_size(box, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
    lv_obj_align(box, LV_ALIGN_CENTER, 0, 0);
    lv_obj_set_style_bg_color(box, ui_color_hex("#1F2A36"), 0);
    lv_obj_set_style_bg_opa(box, LV_OPA_90, 0);
    lv_obj_set_style_border_width(box, 1, 0);
    lv_obj_set_style_border_color(box, ui_color_hex("#3A4A5A"), 0);
    lv_obj_set_style_radius(box, 6, 0);
    lv_obj_set_style_pad_all(box, 16, 0);
    {
        lv_obj_t* l = lv_label_create(box);
        lv_label_set_text(l, text);
        lv_obj_set_style_text_font(l, ui_font(16), 0);
        lv_obj_set_style_text_color(l, ui_color_hex("#E6EDF3"), 0);
        lv_obj_set_style_border_width(l, 0, 0);
        lv_obj_set_style_bg_opa(l, LV_OPA_TRANSP, 0);
    }
    s_toast_box = box;
    s_toast_timer = lv_timer_create(toast_timer_cb, 2000, box);
}

/* ── 长按（events.long_press）──
 * 阈值是每个按钮自己的 long_press_ms，LVGL 只有全局的一份，所以自己起一次性定时器：
 * 到点先执行动作、标记 lp_fired，松开时那次 click 由 action_cb 吞掉（CLICKED 在指针
 * 释放时总会发，不能指望 LVGL 帮你吞）。单点触摸，一份状态够。 */
static lv_timer_t* s_hold_timer;

static void hold_cancel(void)
{
    if (s_hold_timer) {
        lv_timer_delete(s_hold_timer);
        s_hold_timer = NULL;
    }
}

static void hold_fire_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);

    lv_timer_delete(t);
    s_hold_timer = NULL;
    if (!w || !w->obj || !lv_obj_is_valid(w->obj) || w->lp_slot < 0) return;
    w->lp_fired = 1;
    HMI_LOG("button: 长按 %dms 触发", w->long_press_ms);
    page_run_action(w->lp_slot);
}

void hold_begin_cb(lv_event_t* e)          /* LV_EVENT_PRESSED */
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    uint32_t ms;

    if (!w || w->lp_slot < 0) return;
    hold_cancel();
    w->lp_fired = 0;
    ms = (w->long_press_ms >= 300) ? (uint32_t)w->long_press_ms : 800u;
    s_hold_timer = lv_timer_create(hold_fire_cb, ms, w);
}

void hold_end_cb(lv_event_t* e)            /* RELEASED / PRESS_LOST / DELETE */
{
    (void)e;
    hold_cancel();
}

/* ── 二次确认（button props.confirm）──
 * 模态：整屏半透明遮罩吞掉点击，中间固定尺寸的框（按钮位置固定，(300,285) 确定、
 * (500,285) 取消），点确定才走 page_run_action，取消或 wait_ms 到点都不执行。 */
#define CF_W 420
#define CF_H 170

static struct {
    lv_obj_t* overlay;
    lv_timer_t* timer;
    long slot;
} s_cf = { NULL, NULL, -1 };

void confirm_reset(void)
{
    if (s_cf.timer) {
        lv_timer_delete(s_cf.timer);
        s_cf.timer = NULL;
    }
    if (s_cf.overlay && lv_obj_is_valid(s_cf.overlay)) lv_obj_delete(s_cf.overlay);
    s_cf.overlay = NULL;
    s_cf.slot = -1;
}

static void cf_yes_cb(lv_event_t* e)
{
    long slot = s_cf.slot;

    (void)e;
    confirm_reset();
    if (slot >= 0) page_run_action(slot);
}

static void cf_no_cb(lv_event_t* e)
{
    (void)e;
    confirm_reset();
}

static void cf_timeout_cb(lv_timer_t* t)
{
    (void)t;
    s_cf.timer = NULL;                     /* repeat_count=1，本定时器由 LVGL 自删 */
    HMI_LOG("confirm: 超时未确认，自动取消");
    if (s_cf.overlay && lv_obj_is_valid(s_cf.overlay)) lv_obj_delete(s_cf.overlay);
    s_cf.overlay = NULL;
    s_cf.slot = -1;
}

static lv_obj_t* cf_button(lv_obj_t* parent, int x, int y, int w, int h,
                           const char* text, const char* bg, const char* fallback_key)
{
    lv_obj_t* b = lv_button_create(parent);
    lv_obj_t* lb;
    char tb[96];

    if (!b) return NULL;
    lv_obj_set_pos(b, (int32_t)x, (int32_t)y);
    lv_obj_set_size(b, (int32_t)w, (int32_t)h);
    lv_obj_set_style_bg_color(b, ui_color_hex(bg), 0);
    lv_obj_set_style_radius(b, 8, 0);
    lb = lv_label_create(b);
    if (text && *text) i18n_text(text, tb, sizeof(tb));
    else snprintf(tb, sizeof(tb), "%s", i18n_rt(fallback_key));
    lv_label_set_text(lb, tb);
    lv_obj_set_style_text_font(lb, ui_font(16), 0);
    lv_obj_set_style_text_color(lb, ui_color_hex("#FFFFFF"), 0);
    lv_obj_center(lb);
    return b;
}

void confirm_open(widget_t* w, long slot)
{
    lv_obj_t* ov;
    lv_obj_t* box;
    lv_obj_t* lb;
    char tb[200];
    int bx, by;

    if (!w || slot < 0) return;
    confirm_reset();

    ov = lv_obj_create(lv_screen_active());
    if (!ov) return;
    lv_obj_remove_flag(ov, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_pos(ov, 0, 0);
    lv_obj_set_size(ov, HMI_LCD_WIDTH, HMI_LCD_HEIGHT);
    lv_obj_set_style_bg_color(ov, ui_color_hex("#000000"), 0);
    lv_obj_set_style_bg_opa(ov, LV_OPA_40, 0);
    lv_obj_set_style_border_width(ov, 0, 0);
    lv_obj_set_style_radius(ov, 0, 0);
    lv_obj_set_style_pad_all(ov, 0, 0);

    bx = (HMI_LCD_WIDTH - CF_W) / 2;
    by = (HMI_LCD_HEIGHT - CF_H) / 2;
    box = lv_obj_create(ov);
    lv_obj_remove_flag(box, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_pos(box, (int32_t)bx, (int32_t)by);
    lv_obj_set_size(box, CF_W, CF_H);
    lv_obj_set_style_bg_color(box, ui_color_hex("#1F2A36"), 0);
    lv_obj_set_style_border_color(box, ui_color_hex("#3A4A5A"), 0);
    lv_obj_set_style_border_width(box, 1, 0);
    lv_obj_set_style_radius(box, 8, 0);
    lv_obj_set_style_pad_all(box, 0, 0);

    lb = lv_label_create(box);
    lv_label_set_text(lb, i18n_text(w->u.btn.cf_text, tb, sizeof(tb)));
    lv_label_set_long_mode(lb, LV_LABEL_LONG_WRAP);
    lv_obj_set_pos(lb, 20, 20);
    lv_obj_set_size(lb, CF_W - 40, 70);
    lv_obj_set_style_text_font(lb, ui_font(16), 0);
    lv_obj_set_style_text_color(lb, ui_color_hex("#E6EDF3"), 0);
    lv_obj_set_style_border_width(lb, 0, 0);
    lv_obj_set_style_bg_opa(lb, LV_OPA_TRANSP, 0);

    {
        lv_obj_t* b = cf_button(box, 20, 110, 180, 40, w->u.btn.cf_yes, "#2F70D0", "confirm.yes");

        if (b) lv_obj_add_event_cb(b, cf_yes_cb, LV_EVENT_CLICKED, NULL);
        b = cf_button(box, CF_W - 200, 110, 180, 40, w->u.btn.cf_no, "#4A5A6A", "confirm.no");
        if (b) lv_obj_add_event_cb(b, cf_no_cb, LV_EVENT_CLICKED, NULL);
    }

    s_cf.overlay = ov;
    s_cf.slot = slot;
    if (w->u.btn.cf_wait_ms > 0) {
        s_cf.timer = lv_timer_create(cf_timeout_cb, (uint32_t)w->u.btn.cf_wait_ms, NULL);
        if (s_cf.timer) lv_timer_set_repeat_count(s_cf.timer, 1);
    }
    HMI_LOG("confirm: 已弹确认框（%s），等待用户选择", w->u.btn.cf_text);
}

/* 点输入框 → 打开通用数值键盘（ADR-0010）：提交才写变量，取消无副作用 */
/* 日志里用变量名而不是下标：tag id 是 0 基的，"变量 1" 会让人以为写错了变量。 */
static const char* input_tag_name(const widget_t* w)
{
    const tag_def_t* t = s_proj ? project_tag(s_proj, w->bind_tag) : NULL;

    return t ? t->name : "?";
}

static void input_commit(void* ud, double v)
{
    widget_t* w = (widget_t*)ud;
    char text[64];

    if (!w || w->bind_tag < 0) return;
    if (apply_tag_write(w->bind_tag, 0, 0, v) != 0) {
        HMI_LOG("input: 写变量 %s 失败（只读或越界）", input_tag_name(w));
        return;
    }
    /* 提交后立刻把值显示在框里：设计画布不跑刷新定时器，只靠绑定刷新的话框里还是旧文字 */
    if (w->obj) {
        if (w->decimals > 0) snprintf(text, sizeof(text), "%.*f", w->decimals, v);
        else snprintf(text, sizeof(text), "%.0f", v);
        lv_textarea_set_text(w->obj, text);
    }
}

/* mode=text/password 的提交：写字符串变量（本地值 + 设备下发） */
static void input_commit_text(void* ud, const char* s)
{
    widget_t* w = (widget_t*)ud;

    if (!w || w->bind_tag < 0) return;
    if (tagwrite_str(w->bind_tag, s) != 0) {
        HMI_LOG("input: 写字符串变量 %s 失败（只读或不是 string）", input_tag_name(w));
        return;
    }
    /* 同上：提交即显示，别等绑定刷新 */
    if (w->obj) lv_textarea_set_text(w->obj, s);
    HMI_LOG("input: %s 已写入 \"%s\"", input_tag_name(w), s);
}

/* 键盘控件（keyboard）：把内联键盘挂到 target 控件上。
 * 提交路径与点开 input 时完全一致（input_commit / input_commit_text），
 * 所以"点开键盘"和"常驻键盘"写进去的值不会有两套语义。 */
int keyboard_attach(widget_t* kb, widget_t* target, char* err, size_t errsz)
{
    static char s_kb_text[72];       /* 内联键盘会长期持有 req.text，不能用栈 */
    keypad_req_t req;
    const tag_def_t* t;

    if (!kb || !kb->obj || !target || !target->obj) {
        snprintf(err, errsz, "keyboard: target 控件不存在");
        return -1;
    }
    if (target->bind_tag < 0) {
        snprintf(err, errsz, "keyboard: target 控件没有绑定变量");
        return -1;
    }
    t = s_proj ? project_tag(s_proj, target->bind_tag) : NULL;
    memset(&req, 0, sizeof(req));
    if (target->input_text) {
        int cn = tagdb_str_copy(target->bind_tag, s_kb_text,
                                (int)sizeof(s_kb_text) - 1, NULL);

        if (cn < 0) cn = 0;
        s_kb_text[cn] = '\0';
        req.mode = KEYPAD_TEXT;
        req.title = t ? t->name : NULL;
        req.text = s_kb_text;
        req.max_len = target->max_len;
        req.on_commit_text = input_commit_text;
        if (target->input_blur) req.on_close_text = input_commit_text;
        req.ud = target;
    } else {
        req.mode = KEYPAD_NUM;
        req.title = t ? t->name : NULL;
        req.value = tagdb_num(target->bind_tag);
        req.decimals = target->decimals;
        req.has_min = target->has_min;
        req.has_max = target->has_max;
        req.min = target->min;
        req.max = target->max;
        req.on_commit_num = input_commit;
        if (target->input_blur) req.on_close_num = input_commit;
        req.ud = target;
    }
    if (ui_keypad_open_inline(kb->obj, &req) != 0) {
        snprintf(err, errsz, "keyboard: 建不起来（一页只允许一个键盘，且控件框要够大）");
        return -1;
    }
    return 0;
}

void input_click_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    const tag_def_t* t;
    keypad_req_t req;

    if (!w || !w->obj || w->bind_tag < 0) return;
    t = s_proj ? project_tag(s_proj, w->bind_tag) : NULL;

    if (w->input_text) {                      /* mode=text/password：文本键盘写字符串变量 */
        char cur[72] = "";
        int cn = tagdb_str_copy(w->bind_tag, cur, (int)sizeof(cur) - 1, NULL);

        if (cn < 0) cn = 0;
        cur[cn] = '\0';
        /* 现值可能比 max_len 长（设备里的旧值/工程改了限长）：编辑副本按 UTF-8 边界截断，
         * 否则键盘一打开就"已满"，一个字都输不进去。 */
        if (w->max_len > 0 && cn > w->max_len) {
            int k = w->max_len;

            while (k > 0 && ((unsigned char)cur[k] & 0xC0) == 0x80) k--;
            cur[k] = '\0';
            HMI_LOG("input: 变量 %d 的现值 %d 字节超过 max_len=%d，编辑时截断", w->bind_tag,
                    cn, w->max_len);
        }
        memset(&req, 0, sizeof(req));
        req.mode = KEYPAD_TEXT;
        req.title = t ? t->name : NULL;
        req.text = cur;
        req.max_len = w->max_len;
        req.on_commit_text = input_commit_text;
        if (w->input_blur) req.on_close_text = input_commit_text;
        req.ud = w;
        if (ui_keypad_open(&req) != 0)
            HMI_LOG("input: 键盘打不开（没有页面 screen）");
        else
            HMI_LOG("keypad: 文本键盘（max_len=%d）", w->max_len);
        return;
    }

    memset(&req, 0, sizeof(req));
    req.title = t ? t->name : NULL;
    req.value = tagdb_num(w->bind_tag);
    req.decimals = w->decimals;
    req.has_min = w->has_min;
    req.has_max = w->has_max;
    req.min = w->min;
    req.max = w->max;
    req.mode = KEYPAD_NUM;
    req.on_commit_num = input_commit;
    if (w->input_blur) req.on_close_num = input_commit;
    req.ud = w;
    if (ui_keypad_open(&req) != 0)
        HMI_LOG("input: 键盘打不开（没有页面 screen）");
}

/* ── tooltip（公共属性 props.tooltip）──
 * 触摸屏没有 hover：按住 800ms 在控件旁弹一条提示，松开即收。
 * 提示弹出后这次按下算"已被提示消费"：click 不再执行（schema 里写的就是这个语义）。
 * 文字不存进 widget_t——按控件下标回查工程 JSON，省掉每个控件一份字符串。 */
#define TIP_DELAY_MS 800
#define TIP_HOLD_MS  2500

static lv_obj_t*   s_tip_box;
static lv_timer_t* s_tip_delay;

/* 提示文字在 build 时拷进控件：页面 JSON 建完就 free 了，s_wnodes 只是临时视图，
 * 运行期不能再回查（否则就是 use-after-free）。 */
static const char* widget_tip_text(widget_t* w)
{
    if (!w || !w->tip[0]) return NULL;
    return w->tip;
}

static void tip_hide(void)
{
    if (s_tip_delay) {
        lv_timer_delete(s_tip_delay);
        s_tip_delay = NULL;
    }
    if (s_tip_box && lv_obj_is_valid(s_tip_box)) lv_obj_delete(s_tip_box);
    s_tip_box = NULL;
}

static void tip_auto_hide_cb(lv_timer_t* t)
{
    LV_UNUSED(t);
    if (s_tip_box && lv_obj_is_valid(s_tip_box)) lv_obj_delete(s_tip_box);
    s_tip_box = NULL;
}

static void tip_show(widget_t* w, const char* text)
{
    lv_area_t a;
    lv_obj_t* top = lv_layer_top();
    lv_timer_t* t;
    int32_t x, y, tw, th;

    if (!top || !w->obj) return;
    if (s_tip_box && lv_obj_is_valid(s_tip_box)) lv_obj_delete(s_tip_box);
    s_tip_box = lv_label_create(top);
    if (!s_tip_box) return;
    lv_label_set_text(s_tip_box, text);
    lv_obj_set_style_text_font(s_tip_box, ui_font(14), 0);
    lv_obj_set_style_text_color(s_tip_box, ui_color_hex("#E6EDF3"), 0);
    lv_obj_set_style_bg_color(s_tip_box, ui_color_hex("#1B2530"), 0);
    lv_obj_set_style_bg_opa(s_tip_box, LV_OPA_COVER, 0);
    lv_obj_set_style_border_color(s_tip_box, ui_color_hex("#3A4A5A"), 0);
    lv_obj_set_style_border_width(s_tip_box, 1, 0);
    lv_obj_set_style_radius(s_tip_box, 4, 0);
    lv_obj_set_style_pad_all(s_tip_box, 6, 0);
    lv_obj_remove_flag(s_tip_box, LV_OBJ_FLAG_CLICKABLE);

    /* 摆在控件上方；顶上放不下就翻到下方；左右夹在屏内 */
    lv_obj_get_coords(w->obj, &a);
    lv_obj_update_layout(s_tip_box);
    tw = lv_obj_get_width(s_tip_box);
    th = lv_obj_get_height(s_tip_box);
    x = a.x1;
    if (x + tw > HMI_LCD_WIDTH - 4) x = HMI_LCD_WIDTH - 4 - tw;
    if (x < 4) x = 4;
    y = a.y1 - th - 6;
    if (y < 4) y = a.y2 + 6;
    if (y + th > HMI_LCD_HEIGHT - 4) y = HMI_LCD_HEIGHT - 4 - th;
    lv_obj_set_pos(s_tip_box, x, y);

    t = lv_timer_create(tip_auto_hide_cb, TIP_HOLD_MS, NULL);
    if (t) lv_timer_set_repeat_count(t, 1);
}

static void tip_delay_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);
    const char* text;

    s_tip_delay = NULL;
    if (!w || !w->obj) return;
    text = widget_tip_text(w);
    if (!text || !*text) return;
    w->tip_shown = 1;
    tip_show(w, text);
}

void tip_begin_cb(lv_event_t* e)          /* LV_EVENT_PRESSED */
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);

    if (!w || !w->obj) return;
    w->tip_shown = 0;
    if (!widget_tip_text(w)) return;
    if (s_tip_delay) {
        lv_timer_delete(s_tip_delay);
        s_tip_delay = NULL;
    }
    s_tip_delay = lv_timer_create(tip_delay_cb, TIP_DELAY_MS, w);
    if (s_tip_delay) lv_timer_set_repeat_count(s_tip_delay, 1);
}

void tip_end_cb(lv_event_t* e)            /* RELEASED / PRESS_LOST / DELETE */
{
    LV_UNUSED(e);
    tip_hide();
}

/* 可写绑定的自动写回（37 §6.3）：开关、滑块这类控件由用户直接改值，
 * 不需要另写 write_tag 动作。user_data = (tag_id << 8) | is_bool。 */
void writeback_cb(lv_event_t* e)
{
    intptr_t v = (intptr_t)lv_event_get_user_data(e);
    int tag = (int)(v >> 8);
    int is_bool = (int)(v & 0xFF);
    lv_obj_t* obj = lv_event_get_target_obj(e);

    /* 控件自动写回也要过权限：这是"绕过按钮直接改值"的入口（ADR-0012）。
     * 命名位 operate 同样要判（ADR-0026）——组掩码放行但没给 operate 位的用户
     * 不该能拖滑块改设备。 */
    {
        widget_t* w = widget_by_obj(obj);

        if (w && (!auth_can(w->poper) || !auth_perm(PERM_OPERATE))) {
            HMI_LOG("auth: 拒绝写回（用户 %s，%s）",
                    auth_user_name() ? auth_user_name() : "未登录",
                    auth_can(w->poper) ? "缺少 operate 权限" : "控件权限不足");
            audit_denied("writeback",
                         auth_can(w->poper) ? "operate" : "控件 perm 组掩码");
            toast_show(i18n_rt("toast.no_perm"));
            return;
        }
    }
    auth_touch();

    if (is_bool)
        (void)apply_tag_write(tag, 1, lv_obj_has_state(obj, LV_STATE_CHECKED), 0.0);
    else {
        /* 取值的类不同：下拉框取选中项、旋钮取弧值、其余（滑块）取滑块值。
         * 早先只有 slider 一条分支，旋钮写回会读到滑块类的字段（值恒为 0）。 */
        double v = lv_obj_check_type(obj, &lv_roller_class)
                       ? (double)lv_roller_get_selected(obj)
                       : lv_obj_check_type(obj, &lv_arc_class)
                             ? (double)lv_arc_get_value(obj)
                             : (double)lv_slider_get_value(obj);
        double step = 0;

        if (lv_obj_check_type(obj, &lv_slider_class)) {
            /* step 只影响写回值，控件本身仍连续拖动 */
            for (int i = 0; i < s_nwidgets; i++)
                if (s_widgets[i].obj == obj) { step = s_widgets[i].step; break; }
        }
        if (step > 0) v = (double)((long long)(v / step + 0.5)) * step;
        (void)apply_tag_write(tag, 0, 0, v);
    }
}

/* combobox 的 change：把选中项对应的 enum 值写回变量 */
void combo_change_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    const tag_def_t* t;

    if (!w || w->opt_tag < 0 || !s_proj) return;
    t = project_tag(s_proj, w->opt_tag);
    if (!t || t->enum_count == 0) return;
    {
        uint32_t sel = lv_dropdown_get_selected(w->obj);

        if (sel >= (uint32_t)t->enum_count) return;
        (void)apply_tag_write(w->opt_tag, 0, 0, t->enums[sel].v);
    }
}

