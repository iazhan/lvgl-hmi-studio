/*
 * ui_login.c — 内置登录框 + 改密码（ADR-0012）
 *
 * 两种模式共用一块面板：
 *   登录：点用户名即登录（免密直接进，有口令弹文本键盘）
 *   改密：选用户 → 旧口令 → 新口令 → 再输一次 → 写设备副本
 * 改密只服务"知道自己旧口令的人"：免密用户不允许在屏上设口令，
 * 否则谁都能给免密的 admin 加个密码，把它变成自己的账号。
 */
#include "ui_login.h"
#include "core/audit.h"
#include "core/auth.h"
#include "core/i18n.h"
#include "ui_keypad.h"
#include "ui_font.h"
#include "widgets.h"
#include "hmi_log.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define LD_W 460
#define LD_H 380
#define LD_ROW_H 40
#define PWD_MAX 16

static lv_obj_t* s_mask;
static lv_obj_t* s_title;
static lv_obj_t* s_list;
static lv_obj_t* s_msg;
static lv_obj_t* s_btn2;              /* 登录模式下是「改密码」，改密模式下是「取消」 */

/* 改密的状态机 */
static int  s_mode;                   /* 0 登录、1 选用户、2 旧口令、3 新口令、4 再输一次 */
static int  s_target;                 /* 改密目标用户下标 */
static char s_pwd_new[PWD_MAX + 1];

/* ── 前向声明（列表行回调分两种模式） ── */
static void login_row_cb(lv_event_t* e);
static void change_row_cb(lv_event_t* e);
static void rebuild_list(void);
static void set_msg(const char* text, int red);

void login_dialog_reset(void)
{
    s_mask = NULL;
    s_title = NULL;
    s_list = NULL;
    s_msg = NULL;
    s_btn2 = NULL;
    s_mode = 0;
    s_target = -1;
    s_pwd_new[0] = '\0';
}

void login_dialog_close(void)
{
    if (s_mask) lv_obj_delete(s_mask);
    login_dialog_reset();
}

static void set_msg(const char* text, int red)
{
    if (!s_msg) return;
    lv_label_set_text(s_msg, text ? text : "");
    lv_obj_set_style_text_color(s_msg, ui_color_hex(red ? "#E05C5C" : "#8FD694"), 0);
}

static void back_to_login(void)
{
    s_mode = 0;
    s_target = -1;
    s_pwd_new[0] = '\0';
    if (s_title) lv_label_set_text(s_title, i18n_rt("login.title"));
    if (s_btn2) {
        lv_obj_t* l = lv_obj_get_child(s_btn2, 0);

        if (l) lv_label_set_text(l, i18n_rt("login.change"));
    }
    rebuild_list();
}

/* ── 改密流程 ── */

static void prompt_new_pwd(const char* hint);

static void again_commit(void* ud, const char* pwd);

static void new_pwd_first_commit(void* ud, const char* pwd)
{
    keypad_req_t req;

    (void)ud;
    snprintf(s_pwd_new, sizeof(s_pwd_new), "%s", pwd);
    s_mode = 4;
    memset(&req, 0, sizeof(req));
    req.mode = KEYPAD_TEXT;
    req.title = i18n_rt("pwd.again");
    req.text = "";
    req.max_len = PWD_MAX;
    req.min_len = user_password_min_len();
    req.on_commit_text = again_commit;
    (void)ui_keypad_open(&req);
}

static void again_commit(void* ud, const char* pwd)
{
    char err[128];

    (void)ud;
    if (s_mode != 4) return;
    if (strcmp(pwd, s_pwd_new) != 0) {
        /* 弹窗在键盘底下，提示写进键盘自己的提示行才看得见 */
        set_msg(i18n_rt("pwd.mismatch"), 1);
        s_mode = 3;
        prompt_new_pwd(i18n_rt("pwd.mismatch"));
        return;
    }
    if (user_set_password(s_target, s_pwd_new, err, sizeof(err)) != 0) {
        set_msg(err, 1);
        s_mode = 3;
        prompt_new_pwd(err);
        return;
    }
    HMI_LOG("auth: %s 的口令已修改", user_at(s_target) ? user_at(s_target)->name : "?");
    s_mode = 0;
    s_target = -1;
    s_pwd_new[0] = '\0';
    back_to_login();
    set_msg(i18n_rt("pwd.done"), 0);
}

static void prompt_new_pwd(const char* hint)
{
    keypad_req_t req;
    char title[64];

    if (s_mode != 3) return;
    memset(&req, 0, sizeof(req));
    req.mode = KEYPAD_TEXT;
    /* 最短长度来自 manifest.password_min_len，与 user_set_password 的校验同源 */
    snprintf(title, sizeof(title), i18n_rt("pwd.new"), user_password_min_len());
    req.title = title;
    req.text = "";
    req.hint = hint;
    req.max_len = PWD_MAX;
    req.min_len = user_password_min_len();
    req.on_commit_text = new_pwd_first_commit;
    if (ui_keypad_open(&req) != 0) set_msg(i18n_rt("login.kb_fail"), 1);
}

static void old_pwd_commit(void* ud, const char* pwd)
{
    (void)ud;
    if (s_mode != 2) return;
    if (user_verify(s_target, pwd) != 0) {
        set_msg(i18n_rt("pwd.old_bad"), 1);
        back_to_login();
        return;
    }
    s_mode = 3;
    prompt_new_pwd(NULL);
}

static void change_row_cb(lv_event_t* e)
{
    int ui = (int)(intptr_t)lv_event_get_user_data(e);
    const user_t* u = user_at(ui);
    keypad_req_t req;

    if (!u) return;
    if (u->pass[0] == '\0') {
        set_msg(i18n_rt("pwd.no_password"), 1);
        return;
    }
    /* 给别人改口令要 manage_users（改自己不用：旧口令就是身份证明）。
     * 未登录时也放行——那正是"忘了登录、靠旧口令重设"的场景。 */
    {
        const char* me = auth_user_name();

        if (auth_logged_in() && (!me || strcmp(me, u->name) != 0) &&
            !auth_perm(PERM_MANAGE_USERS)) {
            HMI_LOG("auth: 拒绝 %s 给 %s 改口令（缺 manage_users 权限）",
                    me ? me : "?", u->name);
            audit_denied("pwd-change", "manage_users");
            set_msg(i18n_rt("toast.no_perm"), 1);
            return;
        }
    }
    s_target = ui;
    s_mode = 2;
    memset(&req, 0, sizeof(req));
    req.mode = KEYPAD_TEXT;
    req.title = i18n_rt("pwd.old");
    req.text = "";
    req.max_len = PWD_MAX;
    req.on_commit_text = old_pwd_commit;
    if (ui_keypad_open(&req) != 0) set_msg(i18n_rt("login.kb_fail"), 1);
}

/* ── 登录流程 ── */

static void after_login(int rc, const char* name)
{
    if (rc == 0) {
        /* 只关框；页面由主循环检测到身份变化后重建（一处重建，避免连着重画两次） */
        HMI_LOG("auth: 登录 %s", name);
        login_dialog_close();
        return;
    }
    if (rc == -2) {
        char buf[96];

        snprintf(buf, sizeof(buf), i18n_rt("login.locked"), auth_lockout_min());
        set_msg(buf, 1);
    } else {
        set_msg(i18n_rt("login.bad"), 1);
    }
}

static void pwd_commit(void* ud, const char* pwd)
{
    int ui = (int)(intptr_t)ud;
    const user_t* u = user_at(ui);
    int rc;

    if (!u) return;
    rc = auth_login(u->name, pwd);
    after_login(rc, u->name);
}

static void login_row_cb(lv_event_t* e)
{
    int ui = (int)(intptr_t)lv_event_get_user_data(e);
    const user_t* u = user_at(ui);
    keypad_req_t req;

    if (!u) return;
    if (u->pass[0] == '\0') {
        after_login(auth_login(u->name, NULL), u->name);
        return;
    }
    memset(&req, 0, sizeof(req));
    req.mode = KEYPAD_TEXT;
    req.title = u->name;
    req.text = "";
    req.max_len = PWD_MAX;
    req.on_commit_text = pwd_commit;
    req.ud = (void*)(intptr_t)ui;
    if (ui_keypad_open(&req) != 0) set_msg(i18n_rt("login.kb_fail"), 1);
}

/* ── 列表（两种模式共用一个列表，只是行回调与文字不同） ── */

static void rebuild_list(void)
{
    int n, shown;

    if (!s_list) return;
    lv_obj_clean(s_list);
    n = user_count();
    shown = n > 6 ? 6 : n;
    for (int i = 0; i < shown; i++) {
        const user_t* u = user_at(i);
        lv_obj_t* row;
        lv_obj_t* lb;
        char buf[96];

        if (!u) break;
        row = lv_obj_create(s_list);
        if (!row) break;
        lv_obj_set_pos(row, 0, i * LD_ROW_H);
        lv_obj_set_size(row, LD_W - 30, LD_ROW_H - 4);
        lv_obj_remove_flag(row, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_set_style_bg_color(row, ui_color_hex("#243140"), 0);
        lv_obj_set_style_bg_opa(row, LV_OPA_COVER, 0);
        lv_obj_set_style_border_width(row, 0, 0);
        lv_obj_set_style_radius(row, 6, 0);
        lv_obj_set_style_pad_all(row, 0, 0);
        lv_obj_add_event_cb(row, s_mode == 1 ? change_row_cb : login_row_cb,
                            LV_EVENT_CLICKED, (void*)(intptr_t)i);

        if (s_mode == 1)
            snprintf(buf, sizeof(buf), i18n_rt("login.row_change"), u->name);
        else
            snprintf(buf, sizeof(buf), "%s%s", u->name, u->pass[0] ? "" : i18n_rt("login.no_pwd"));
        lb = lv_label_create(row);
        if (lb) {
            lv_label_set_text(lb, buf);
            lv_obj_set_style_text_font(lb, ui_font(16), 0);
            lv_obj_set_style_text_color(lb, ui_color_hex("#E6EDF3"), 0);
            lv_obj_set_style_bg_opa(lb, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(lb, 0, 0);
            lv_obj_set_pos(lb, 10, (LD_ROW_H - 4 - 16) / 2);
        }
    }
    if (n > shown) {
        lv_obj_t* more = lv_label_create(s_list);

        if (more) {
            lv_label_set_text(more, i18n_rt("login.more"));
            lv_obj_set_style_text_font(more, ui_font(10), 0);
            lv_obj_set_style_text_color(more, ui_color_hex("#9AA7B5"), 0);
            lv_obj_set_style_bg_opa(more, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(more, 0, 0);
            lv_obj_set_pos(more, 4, shown * LD_ROW_H + 2);
        }
    }
}

static void mask_click_cb(lv_event_t* e)
{
    (void)e;
    login_dialog_close();
}

static void btn2_cb(lv_event_t* e)
{
    lv_obj_t* l;

    (void)e;
    if (s_mode == 1) {                   /* 改密模式下的「取消」 */
        back_to_login();
        set_msg("", 0);
        return;
    }
    /* 登录模式下的「改密码」 */
    s_mode = 1;
    s_target = -1;
    if (s_title) lv_label_set_text(s_title, i18n_rt("login.pick"));
    set_msg(i18n_rt("login.pick_tip"), 0);
    l = s_btn2 ? lv_obj_get_child(s_btn2, 0) : NULL;
    if (l) lv_label_set_text(l, i18n_rt("login.cancel"));
    rebuild_list();
}

static lv_obj_t* mk_btn(lv_obj_t* parent, int x, int y, int w, int h, const char* text,
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
            lv_obj_set_style_text_font(l, ui_font(14), 0);
            lv_obj_set_style_text_color(l, ui_color_hex("#FFFFFF"), 0);
            lv_obj_set_style_bg_opa(l, LV_OPA_TRANSP, 0);
            lv_obj_set_style_border_width(l, 0, 0);
            lv_obj_center(l);
        }
    }
    return b;
}

void login_dialog_open(void)
{
    lv_obj_t* box;

    if (!auth_enabled() || auth_logged_in()) return;
    if (s_mask) {
        lv_obj_move_foreground(s_mask);
        return;
    }

    s_mode = 0;
    s_target = -1;
    s_pwd_new[0] = '\0';

    s_mask = lv_obj_create(lv_screen_active());
    if (!s_mask) return;
    lv_obj_set_pos(s_mask, 0, 0);
    lv_obj_set_size(s_mask, lv_pct(100), lv_pct(100));
    lv_obj_remove_flag(s_mask, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_color(s_mask, ui_color_hex("#000000"), 0);
    lv_obj_set_style_bg_opa(s_mask, LV_OPA_50, 0);
    lv_obj_set_style_border_width(s_mask, 0, 0);
    lv_obj_set_style_radius(s_mask, 0, 0);
    lv_obj_set_style_pad_all(s_mask, 0, 0);
    lv_obj_add_event_cb(s_mask, mask_click_cb, LV_EVENT_CLICKED, NULL);

    box = lv_obj_create(s_mask);
    if (!box) {
        login_dialog_close();
        return;
    }
    lv_obj_set_size(box, LD_W, LD_H);
    lv_obj_center(box);
    lv_obj_remove_flag(box, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_color(box, ui_color_hex("#1B2530"), 0);
    lv_obj_set_style_bg_opa(box, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(box, 1, 0);
    lv_obj_set_style_border_color(box, ui_color_hex("#3A4A5A"), 0);
    lv_obj_set_style_radius(box, 8, 0);
    lv_obj_set_style_pad_all(box, 10, 0);

    s_title = lv_label_create(box);
    if (s_title) {
        lv_label_set_text(s_title, i18n_rt("login.title"));
        lv_obj_set_pos(s_title, 0, 0);
        lv_obj_set_style_text_font(s_title, ui_font(18), 0);
        lv_obj_set_style_text_color(s_title, ui_color_hex("#E6EDF3"), 0);
        lv_obj_set_style_bg_opa(s_title, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(s_title, 0, 0);
    }

    s_list = lv_obj_create(box);
    if (!s_list) {
        login_dialog_close();
        return;
    }
    lv_obj_set_pos(s_list, 0, 26);
    lv_obj_set_size(s_list, LD_W - 20, 244);      /* 26..270 */
    lv_obj_set_style_bg_opa(s_list, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(s_list, 0, 0);
    lv_obj_set_style_pad_all(s_list, 0, 0);
    ui_scroll_area(s_list, 1);

    s_msg = lv_label_create(box);
    if (s_msg) {
        lv_label_set_text(s_msg, "");
        lv_obj_set_style_text_font(s_msg, ui_font(12), 0);
        lv_obj_set_style_text_color(s_msg, ui_color_hex("#9AA7B5"), 0);
        lv_obj_set_style_bg_opa(s_msg, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(s_msg, 0, 0);
        lv_obj_set_pos(s_msg, 0, 274);            /* 274..310 */
        lv_obj_set_size(s_msg, LD_W - 20, 36);
        lv_label_set_long_mode(s_msg, LV_LABEL_LONG_WRAP);
    }

    /* 两个按钮居中成对：150 + 20 + 150 = 320，左右各留 60，不贴右边 */
    s_btn2 = mk_btn(box, 60, 314, 150, 34, i18n_rt("login.change"), "#5A6B7A");
    if (s_btn2) lv_obj_add_event_cb(s_btn2, btn2_cb, LV_EVENT_CLICKED, NULL);
    {
        lv_obj_t* cancel = mk_btn(box, 230, 314, 150, 34, i18n_rt("login.close"), "#3A4A5A");

        if (cancel) lv_obj_add_event_cb(cancel, mask_click_cb, LV_EVENT_CLICKED, NULL);
    }

    rebuild_list();
}
