/*
 * ui_page_actions.c — 动作/事件：解析 actions 数组，按事件码执动作，配方的快捷入口
 */
#include "ui_page.h"
#include "ui_page_internal.h"
#include "comm/modbus.h"
#include "script/lua_rt.h"
#include "core/tagdb.h"
#include "core/sysvar.h"
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

int apply_tag_write(int tag_id, int is_bool, int b, double n)
{
    return is_bool ? tagwrite_bool(tag_id, b) : tagwrite_num(tag_id, n);
}

/* 直接写设备（37 §6.7 write_device）：不建变量也能写，sim 与 modbus 都支持 */
static void apply_device_write(const page_action_t* a)
{
    const device_def_t* d = &s_proj->devices[a->dev];
    int vb = a->value_bool;
    double vn = a->value_num;

    if (a->from_tag >= 0) {          /* 值来自变量：触发这一刻取当前值 */
        vb = tagdb_bool(a->from_tag);
        vn = tagdb_num(a->from_tag);
    }
    if (strcmp(d->protocol, "sim") == 0) {
        if (sim_write(s_sim, a->area, a->addr, a->datatype, vn, vb) != 0)
            HMI_LOG("action: 写 sim %s 失败（area=%d addr=%d）",
                    d->id, a->area, a->addr);
    } else if (modbus_write_device(s_proj, d->id, a->area, a->addr, a->datatype, vn, vb) != 0) {
        HMI_LOG("action: 写设备 %s 失败（area=%d addr=%d）", d->id, a->area, a->addr);
    }
}

/* U 盘目录：宿主上用 HMI_USB_DIR 指一个目录做测试，板子上用挂载点（先挂载） */
static const char* usb_recipe_dir(int* need_mount)
{
    const char* env = getenv("HMI_USB_DIR");

    if (env && *env) {
        *need_mount = 0;
        return env;
    }
    *need_mount = 1;
    return "/var/run/hmi-usb";
}

int hmi_datalog_export(const char* group, int last_min, uint32_t from_ts, uint32_t to_ts,
                       char* err, size_t errsz)
{
    int need_mount = 0;
    const char* dir = usb_recipe_dir(&need_mount);
    int rows;

    if (need_mount && hmi_usb_mount(1) != 0) {
        snprintf(err, errsz, "没插 U 盘");
        return -2;
    }
    rows = savecfg_export_csv(group, last_min, from_ts, to_ts, dir, err, errsz);
    if (need_mount) hmi_usb_umount();
    return rows;
}

/* 配方数据变了：当前页上所有配方表重填一遍（存为新配方/新建/下载之后都要） */
static void refresh_recipe_tables(void)
{
    for (int i = 0; i < s_nwidgets; i++)
        if (s_widgets[i].type == W_RECIPE_TABLE) recipe_table_refresh(&s_widgets[i]);
}

/* 新建配方：文本键盘提交名字 → 采集当前变量值 → 落盘 → 提示 + 刷新 */
static void new_recipe_name_cb(void* ud, const char* name)
{
    int gi = (int)(intptr_t)ud;
    const recipe_group_t* g = recipe_at(gi);
    char msg[160];
    int idx;

    if (!g) return;
    for (int i = 0; i < g->nitems; i++) {
        if (strcmp(g->meta[i].name, name) == 0) {
            toast_show(i18n_rt("toast.rcp_dup"));
            return;
        }
    }
    idx = recipe_add(gi, name, msg, sizeof(msg));
    if (idx < 0) {
        HMI_LOG("recipe: %s", msg);
        toast_show(msg);
        return;
    }
    if (recipe_set_current(gi, idx) == 0 && recipe_save(gi, msg, sizeof(msg)) != 0) {
        HMI_LOG("recipe: 保存失败：%s", msg);
        toast_show(i18n_rt("toast.save_fail"));
        return;
    }
    toast_show(i18n_rt("toast.rcp_new"));
    refresh_recipe_tables();
}

void action_cb(lv_event_t* e)
{
    long idx = (long)(intptr_t)lv_event_get_user_data(e);
    lv_event_code_t code = lv_event_get_code(e);

    if (idx < 0 || idx >= s_nactions) return;

    if (code == LV_EVENT_CLICKED) {
        widget_t* w = widget_by_obj(lv_event_get_current_target(e));

        if (w) {
            /* tooltip 优先级最高：这次按下已经弹过提示，click 不再执行 */
            if (w->tip_shown) {
                w->tip_shown = 0;
                HMI_LOG("tooltip: 本次按下已弹提示，忽略 click");
                return;
            }
            /* 这次按下已经由长按定时器执行过：松开时的那次 click 不再执行 */
            if (w->lp_slot >= 0 && w->lp_fired) {
                w->lp_fired = 0;
                HMI_LOG("button: 本次按下已按长按处理，忽略 click");
                return;
            }
            /* 长按生效（37 §6.10）：click 要看按住多久，不够就不算 */
            if (w->hold_ms > 0) {
                uint32_t held = lv_tick_elaps(w->u.btn.press_tick);

                if (held < (uint32_t)w->hold_ms) {
                    HMI_LOG("button: 按住 %ums < %dms，忽略本次点击", (unsigned)held,
                            w->hold_ms);
                    return;
                }
            }
            /* 二次确认：先弹框，用户点"确定"后才走下面同一套执行路径。
             * 只有按钮族有 cf_*（族字段在 union 里，别的类型读到的是别人的数据） */
            if (w->type == W_BUTTON && w->u.btn.cf_text[0]) {
                confirm_open(w, idx);
                return;
            }
        }
    }
    page_run_action(idx);
}

/* 动作 → 需要的命名权限位（ADR-0026）。0 = 不门控：
 * 登录/登出（权限体系的门不能自己锁自己）、提示、切语言。 */
static perm_bits_t action_perm_bit(int kind)
{
    switch (kind) {
    case ACT_TAG:
    case ACT_TOGGLE_TAG:
    case ACT_ADD_VAR:
    case ACT_SET_BIT:
    case ACT_CLEAR_BIT:
    case ACT_LUA:
        return PERM_OPERATE;
    case ACT_DEVICE:
        return PERM_DEBUG;         /* 直接写设备寄存器：绕过变量表与量程的调试口 */
    case ACT_GOTO_PAGE:
    case ACT_OPEN_PAGE:
    case ACT_CLOSE_PAGE:
    case ACT_BACK:
        return PERM_VIEW;
    case ACT_ACK_ALARM:
        return PERM_ACK_ALARM;
    case ACT_SHELVE_ALARM:
        return PERM_SHELVE;
    case ACT_LOAD_RECIPE:
    case ACT_SAVE_RECIPE:
    case ACT_NEW_RECIPE:
    case ACT_IMPORT_RECIPES:
        return PERM_EDIT_RECIPE;   /* 导入会改配方数据，与编辑同权 */
    case ACT_EXPORT_RECIPES:
    case ACT_EXPORT_DATALOG:
        return PERM_EXPORT;
    case ACT_SAVE_NOW:
        return PERM_CHANGE_CONFIG;
    default:
        return 0;
    }
}

/* 动作名：日志与审计里要能直接看出拒的是哪一类动作 */
static const char* action_name(int kind)
{
    switch (kind) {
    case ACT_TAG:            return "write_tag";
    case ACT_DEVICE:         return "write_device";
    case ACT_TOGGLE_TAG:     return "toggle_tag";
    case ACT_ADD_VAR:        return "add_var";
    case ACT_SET_BIT:        return "set_bit";
    case ACT_CLEAR_BIT:      return "clear_bit";
    case ACT_GOTO_PAGE:      return "goto_page";
    case ACT_OPEN_PAGE:      return "open_page";
    case ACT_CLOSE_PAGE:     return "close_page";
    case ACT_BACK:           return "back";
    case ACT_MESSAGE:        return "show_message";
    case ACT_LUA:            return "script";
    case ACT_ACK_ALARM:      return "ack_alarm";
    case ACT_SHELVE_ALARM:   return "shelve_alarm";
    case ACT_LOAD_RECIPE:    return "load_recipe";
    case ACT_SAVE_RECIPE:    return "save_recipe";
    case ACT_NEW_RECIPE:     return "new_recipe";
    case ACT_EXPORT_RECIPES: return "export_recipes";
    case ACT_IMPORT_RECIPES: return "import_recipes";
    case ACT_SAVE_NOW:       return "save_now";
    case ACT_EXPORT_DATALOG: return "export_datalog";
    case ACT_LOGIN:          return "login";
    case ACT_LOGOUT:         return "logout";
    case ACT_SET_LANGUAGE:   return "set_language";
    default:                 return "action";
    }
}

/* 执行一个动作槽：权限 → 分派。长按定时器、确认框都直接调这里，避开 click 的几道闸 */
void page_run_action(long idx)
{
    const page_action_t* a;

    if (idx < 0 || idx >= s_nactions) return;
    a = &s_actions[idx];

    /* 权限（ADR-0012）：界面上的隐藏/禁用只是第一道防线，这里再判一次 */
    if (!auth_can(a->perm_op)) {
        HMI_LOG("auth: 拒绝动作（用户 %s，控件权限不足）",
                auth_user_name() ? auth_user_name() : "未登录");
        audit_denied(action_name(a->kind), "控件 perm 组掩码不含当前用户");
        toast_show(i18n_rt("toast.no_perm"));
        return;
    }
    /* 命名权限位（ADR-0026）：只在用户表声明了 perms[] 时生效，
     * 老工程（没有 perms）行为不变 */
    {
        perm_bits_t need = action_perm_bit(a->kind);

        if (need && !auth_perm(need)) {
            HMI_LOG("auth: 拒绝动作 %s（用户 %s，缺少 %s 权限）", action_name(a->kind),
                    auth_user_name() ? auth_user_name() : "未登录", perm_bit_name(need));
            audit_denied(action_name(a->kind), perm_bit_name(need));
            toast_show(i18n_rt("toast.no_perm"));
            return;
        }
    }
    auth_touch();

    switch (a->kind) {
    case ACT_LOAD_RECIPE:
    case ACT_SAVE_RECIPE:
    case ACT_NEW_RECIPE: {
        char grp[64];
        char nm[64];
        char msg[160];
        const char* bar = strchr(a->text, '|');
        int gi;

        snprintf(grp, sizeof(grp), "%s", a->text);
        if (bar) {
            grp[(size_t)(bar - a->text)] = '\0';
            snprintf(nm, sizeof(nm), "%s", bar + 1);
        } else {
            nm[0] = '\0';
        }
        gi = recipe_find(grp);
        if (gi < 0) {
            HMI_LOG("recipe: 动作找不到配方组 %s", grp);
            break;
        }
        if (a->kind == ACT_LOAD_RECIPE) {
            int idx = -1;
            const recipe_group_t* g = recipe_at(gi);

            for (int i = 0; g && i < g->nitems; i++)
                if (strcmp(g->meta[i].name, nm) == 0) { idx = i; break; }
            if (idx < 0) {
                HMI_LOG("recipe: 组 %s 里没有配方 %s", grp, nm);
                toast_show(i18n_rt("toast.rcp_missing"));
                break;
            }
            if (recipe_activate(gi, idx, msg, sizeof(msg)) != 0) {
                HMI_LOG("recipe: %s", msg);
                toast_show(i18n_rt("toast.dl_fail"));
                break;
            }
            toast_show(i18n_rt("toast.rcp_loaded"));
            refresh_recipe_tables();
        } else if (a->kind == ACT_NEW_RECIPE) {
            keypad_req_t req;

            memset(&req, 0, sizeof(req));
            req.mode = KEYPAD_TEXT;
            req.title = "新配方名（A-Z 0-9 . - _）";
            req.text = "";
            req.max_len = RCP_NAME_MAX - 1;
            req.on_commit_text = new_recipe_name_cb;
            req.ud = (void*)(intptr_t)gi;
            if (ui_keypad_open(&req) != 0) {
                HMI_LOG("recipe: 键盘打不开（没有页面 screen）");
                toast_show(i18n_rt("login.kb_fail"));
            }
        } else {
            int idx = recipe_add(gi, nm, msg, sizeof(msg));

            if (idx < 0) {
                HMI_LOG("recipe: %s", msg);
                toast_show(msg);
                break;
            }
            if (recipe_set_current(gi, idx) == 0 && recipe_save(gi, msg, sizeof(msg)) != 0) {
                HMI_LOG("recipe: 保存失败：%s", msg);
                toast_show(i18n_rt("toast.save_fail"));
                break;
            }
            toast_show(i18n_rt("toast.rcp_saved"));
            refresh_recipe_tables();
        }
        break;
    }
    case ACT_EXPORT_DATALOG: {
        /* text = "组|last_min|from_ts|to_ts" */
        char grp[SAVECFG_NAME_MAX];
        long lm = 0, ft = 0, tt = 0;
        char err[160];
        int rows;

        memset(grp, 0, sizeof(grp));
        if (sscanf(a->text, "%31[^|]|%ld|%ld|%ld", grp, &lm, &ft, &tt) < 1 || !grp[0]) break;
        rows = hmi_datalog_export(grp, (int)lm, (uint32_t)ft, (uint32_t)tt, err, sizeof(err));
        if (rows == -2) {
            toast_show(i18n_rt("toast.no_usb"));
        } else if (rows < 0) {
            HMI_LOG("savecfg: 导出失败：%s", err);
            toast_show(i18n_rt("toast.usb_out_fail"));
        } else if (rows == 0) {
            toast_show(i18n_rt("toast.export_empty"));
        } else {
            char buf[96];

            /* 带上行数：现场一眼能看出"导的是不是这次的数据" */
            snprintf(buf, sizeof(buf), i18n_rt("toast.datalog_out"), rows);
            toast_show(buf);
        }
        break;
    }
    case ACT_SAVE_NOW: {
        int n = savecfg_sample_now(a->text[0] ? a->text : NULL);

        if (n < 0) {
            HMI_LOG("savecfg: 没有存盘组 %s", a->text);
            toast_show(i18n_rt("toast.save_none"));
        } else {
            toast_show(i18n_rt("toast.saved"));
        }
        break;
    }
    case ACT_SET_LANGUAGE: {
        char msg[96];

        if (i18n_switch(a->text, msg, sizeof(msg)) != 0) {
            HMI_LOG("i18n: %s", msg);
            toast_show(msg);
            break;
        }
        /* 重建推迟一个刷新周期：现在拆整页，LVGL 还拿着正在派发的这个控件对象 */
        page_mgr_request_reload(i18n_rt("toast.lang"));
        break;
    }
    case ACT_LOGIN:
        login_dialog_open();
        break;
    case ACT_LOGOUT:
        auth_logout();                    /* 重建由主循环检测到身份变化后做 */
        toast_show(i18n_rt("toast.logged_out"));
        break;
    case ACT_EXPORT_RECIPES:
    case ACT_IMPORT_RECIPES: {
        int need_mount = 0;
        const char* dir = usb_recipe_dir(&need_mount);
        char msg[160];
        int n;

        if (need_mount && hmi_usb_mount(a->kind == ACT_EXPORT_RECIPES) != 0) {
            toast_show(i18n_rt("toast.no_usb"));
            break;
        }
        if (a->kind == ACT_EXPORT_RECIPES) {
            n = recipe_export(dir, msg, sizeof(msg));
            if (need_mount) hmi_usb_umount();
            if (n <= 0) {
                HMI_LOG("recipe: 导出失败：%s", msg);
                toast_show(i18n_rt("toast.usb_out_fail"));
            } else {
                toast_show(i18n_rt("toast.usb_out"));
            }
        } else {
            n = recipe_import(dir, msg, sizeof(msg));
            if (need_mount) hmi_usb_umount();
            if (n <= 0) {
                HMI_LOG("recipe: 导入失败：%s", msg);
                toast_show(i18n_rt("toast.usb_in_none"));
            } else {
                toast_show(i18n_rt("toast.usb_in"));
                refresh_recipe_tables();
            }
        }
        break;
    }
    case ACT_ACK_ALARM:
        /* alarm = -1 表示该组全部确认（ADR-0008 决定 7） */
        if (a->tag_id < 0) (void)alarm_ack_all(a->text);
        else (void)alarm_ack(a->tag_id);
        break;
    case ACT_SHELVE_ALARM: {
        /* 搁置（46 §8.1）：gate 与其它动作一致——控件/页面的 operate 掩码在
         * page_run_action 入口已经判过，这里只管执行 */
        int mins = (int)(a->value_num + 0.5);

        if (a->tag_id >= 0) {
            if (alarm_shelve(a->tag_id, mins, auth_user_name()) != 0)
                toast_show(i18n_rt("toast.op_fail"));
        } else {
            int n = 0;

            for (int i = 0; i < alarm_rule_count(); i++) {
                int lvl = 0, typ = -1;

                /* 只搁置当前在活动表里的规则（该组全部） */
                if (alarm_rule_info(i, NULL, 0, NULL, 0, &lvl, &typ) != 0) continue;
                if (alarm_shelve(i, mins, auth_user_name()) == 0) n++;
            }
            HMI_LOG("alarm: 搁置 %d 条规则 %d 分钟", n, mins);
        }
        break;
    }
    case ACT_DEVICE:
        apply_device_write(a);
        break;
    case ACT_TOGGLE_TAG:
        (void)apply_tag_write(a->tag_id, 1, !tagdb_bool(a->tag_id), 0.0);
        break;
    case ACT_SET_BIT:
    case ACT_CLEAR_BIT: {
        const tag_t* t = s_proj ? tagdb_at(a->tag_id) : NULL;
        int rc;

        if (t && t->type == TAG_T_BOOL) {
            /* 布尔变量只有第 0 位：走 bool 写，别用 write_num（会被类型检查拒掉） */
            rc = apply_tag_write(a->tag_id, 1, a->kind == ACT_SET_BIT, 0.0);
        } else {
            long long v = (long long)tagdb_num(a->tag_id);
            long long m = 1LL << a->bit;

            v = (a->kind == ACT_SET_BIT) ? (v | m) : (v & ~m);
            rc = apply_tag_write(a->tag_id, 0, 0, (double)v);
        }
        if (rc != 0)
            HMI_LOG("button: %s 变量 %d 失败（只读或越界）",
                    a->kind == ACT_SET_BIT ? "置位" : "清位", a->tag_id);
        break;
    }
    case ACT_LUA: {
        char lerr[256];

        if (lua_rt_dostring(a->text, "=<event>.script", lerr, sizeof(lerr)) != 0)
            HMI_LOG("event script 失败：%s", lerr);
        break;
    }
    case ACT_ADD_VAR: {
        double d = (a->from_tag >= 0) ? tagdb_num(a->from_tag) : a->delta;
        (void)apply_tag_write(a->tag_id, 0, 0, tagdb_num(a->tag_id) + d);
        break;
    }
    case ACT_GOTO_PAGE:
        page_mgr_request(PAGE_OP_GOTO, a->page);
        break;
    case ACT_OPEN_PAGE:
        page_mgr_request(PAGE_OP_OPEN, a->page);
        break;
    case ACT_CLOSE_PAGE:
        page_mgr_request(PAGE_OP_CLOSE, NULL);
        break;
    case ACT_BACK:
        page_mgr_request(PAGE_OP_BACK, NULL);
        break;
    case ACT_MESSAGE:
        toast_show(a->text);
        break;
    default:
        apply_tag_write(a->tag_id, a->value_is_bool, a->value_bool, a->value_num);
        break;
    }
}

static int area_from_name(const char* s)
{
    if (!s) return -1;
    if (strcmp(s, "coil") == 0) return AREA_COIL;
    if (strcmp(s, "discrete") == 0) return AREA_DISCRETE;
    if (strcmp(s, "input") == 0) return AREA_INPUT;
    if (strcmp(s, "holding") == 0) return AREA_HOLDING;
    return -1;
}

static int dt_from_name(const char* s)
{
    static const char* const names[] = {
        "bool", "i16", "u16", "i32", "u32", "f32", "i64", "double", "string"
    };

    if (!s) return -1;
    for (int i = 0; i < DT__COUNT; i++)
        if (strcmp(s, names[i]) == 0) return i;
    return -1;
}

/* write_device：直接写设备寄存器/线圈，不要求先建变量（37 §6.7）。
 * v1 只支持单个 value；from_tag 与 values 数组后置。 */
static int parse_device_action(const hmi_json_t* a, const char* wid,
                               char* err, size_t errsz)
{
    const char* dev = hmi_json_str(hmi_json_get(a, "device"), NULL);
    const char* area_s = hmi_json_str(hmi_json_get(a, "area"), NULL);
    const char* dt_s = hmi_json_str(hmi_json_get(a, "datatype"), NULL);
    const hmi_json_t* val = hmi_json_get(a, "value");
    page_action_t* pa;
    long addr;
    int di = -1, area, dt, from_tag = -1;

    if (!dev) {
        snprintf(err, errsz, "控件 %s: write_device 缺 device", wid);
        return -1;
    }
    for (int i = 0; i < s_proj->devices_count; i++)
        if (strcmp(s_proj->devices[i].id, dev) == 0) { di = i; break; }
    if (di < 0) {
        snprintf(err, errsz, "控件 %s: write_device 的设备 %s 不存在", wid, dev);
        return -1;
    }
    area = area_from_name(area_s);
    if (area < 0) {
        snprintf(err, errsz, "控件 %s: write_device 的 area %s 非法",
                 wid, area_s ? area_s : "(缺失)");
        return -1;
    }
    if (area == AREA_DISCRETE || area == AREA_INPUT) {
        snprintf(err, errsz, "控件 %s: area %s 是只读区，不能写", wid, area_s);
        return -1;
    }
    dt = dt_from_name(dt_s);
    if (dt < 0 || dt == DT_I64 || dt == DT_DOUBLE || dt == DT_STRING) {
        snprintf(err, errsz, "控件 %s: write_device 的 datatype %s 暂不支持（v1 只到 32 位）",
                 wid, dt_s ? dt_s : "(缺失)");
        return -1;
    }
    addr = hmi_json_int(hmi_json_get(a, "addr"), -1);
    if (addr < 0 || addr > 65535) {
        snprintf(err, errsz, "控件 %s: write_device 的 addr 越界（0..65535）", wid);
        return -1;
    }
    /* 一次写一个值：count/values 是批量写，明确报错而不是静默只写第一个 */
    if (hmi_json_get(a, "count") != NULL || hmi_json_get(a, "values") != NULL) {
        snprintf(err, errsz, "控件 %s: write_device 的 count/values 暂未实现（v1 一次写一个值）",
                 wid);
        return -1;
    }
    {
        const hmi_json_t* ft = hmi_json_get(a, "from_tag");
        long tid = (ft && hmi_json_type(ft) == HMI_JSON_NUM) ? hmi_json_int(ft, -1) : -1;

        if (ft && (tid < 0 || tid >= tagdb_count())) {
            snprintf(err, errsz, "控件 %s: write_device 的 from_tag 不是有效变量", wid);
            return -1;
        }
        from_tag = (int)tid;
    }
    if (from_tag < 0 && hmi_json_type(val) != HMI_JSON_BOOL && hmi_json_type(val) != HMI_JSON_NUM) {
        snprintf(err, errsz, "控件 %s: write_device 要么给 value（布尔/数值）要么给 from_tag",
                 wid);
        return -1;
    }
    if (s_nactions >= WIDGET_MAX_PER_PAGE) {
        snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
        return -1;
    }
    pa = &s_actions[s_nactions];
    memset(pa, 0, sizeof(*pa));
    pa->kind = ACT_DEVICE;
    pa->dev = di;
    pa->area = area;
    pa->addr = (int)addr;
    pa->datatype = dt;
    pa->from_tag = from_tag;
    if (from_tag >= 0) {
        /* 值在触发时取变量当前值；布尔变量(线圈)走 bool 写，其余走寄存器写 */
        const tag_t* ft = tagdb_at(from_tag);

        pa->value_is_bool = (ft && ft->type == TAG_T_BOOL) ? 1 : 0;
    } else if (hmi_json_type(val) == HMI_JSON_BOOL) {
        pa->value_is_bool = 1;
        pa->value_bool = hmi_json_bool(val, 0);
    } else {
        pa->value_num = hmi_json_num(val, 0);
    }
    s_nactions++;
    return (int)s_nactions;
}

/* 切页动作：goto_page / open_page 要 page；close_page / back 不需要 */
static int parse_page_action(const hmi_json_t* a, const char* do_s, const char* wid,
                             char* err, size_t errsz)
{
    const char* page = hmi_json_str(hmi_json_get(a, "page"), NULL);
    page_action_t* pa;
    int kind;

    if (strcmp(do_s, "goto_page") == 0) kind = ACT_GOTO_PAGE;
    else if (strcmp(do_s, "open_page") == 0) kind = ACT_OPEN_PAGE;
    else if (strcmp(do_s, "close_page") == 0) kind = ACT_CLOSE_PAGE;
    else kind = ACT_BACK;

    if (kind != ACT_BACK && kind != ACT_CLOSE_PAGE && (!page || !*page)) {
        snprintf(err, errsz, "控件 %s: %s 缺 page 参数", wid, do_s);
        return -1;
    }
    if (s_nactions >= WIDGET_MAX_PER_PAGE) {
        snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
        return -1;
    }
    pa = &s_actions[s_nactions];
    memset(pa, 0, sizeof(*pa));
    pa->kind = kind;
    pa->from_tag = -1;
    if (page) snprintf(pa->page, sizeof(pa->page), "%s", page);
    s_nactions++;
    return (int)s_nactions;
}

/* 变量类动作：toggle_tag（布尔取反）、add_var（加/减常量或另一个变量） */
static int parse_tag_op(const hmi_json_t* a, const char* do_s, const char* wid,
                        char* err, size_t errsz)
{
    long tag = hmi_json_int(hmi_json_get(a, "tag"), -1);
    page_action_t* pa;

    if (tag < 0 || tag >= s_proj->tags_count) {
        snprintf(err, errsz, "控件 %s: %s 的 tag %ld 不存在", wid, do_s, tag);
        return -1;
    }
    if (s_nactions >= WIDGET_MAX_PER_PAGE) {
        snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
        return -1;
    }
    pa = &s_actions[s_nactions];
    memset(pa, 0, sizeof(*pa));
    pa->tag_id = (int)tag;
    pa->from_tag = -1;
    if (strcmp(do_s, "toggle_tag") == 0) {
        pa->kind = ACT_TOGGLE_TAG;
    } else if (strcmp(do_s, "set_bit") == 0 || strcmp(do_s, "clear_bit") == 0) {
        long bit = hmi_json_int(hmi_json_get(a, "bit"), -1);

        if (bit < 0 || bit > 31) {
            snprintf(err, errsz, "控件 %s: %s 的 bit %ld 越界（0..31）", wid, do_s, bit);
            return -1;
        }
        pa->kind = (do_s[0] == 's') ? ACT_SET_BIT : ACT_CLEAR_BIT;
        pa->bit = (int)bit;
    } else {
        pa->kind = ACT_ADD_VAR;
        if (hmi_json_get(a, "from_tag") != NULL) {
            long src = hmi_json_int(hmi_json_get(a, "from_tag"), -1);

            if (src < 0 || src >= s_proj->tags_count) {
                snprintf(err, errsz, "控件 %s: add_var 的 from_tag %ld 不存在", wid, src);
                return -1;
            }
            pa->from_tag = (int)src;
        } else {
            pa->delta = hmi_json_num(hmi_json_get(a, "delta"), 1.0);
        }
    }
    s_nactions++;
    return (int)s_nactions;
}

static int parse_message(const hmi_json_t* a, const char* wid,
                         char* err, size_t errsz)
{
    const char* text = hmi_json_str(hmi_json_get(a, "text"), NULL);
    page_action_t* pa;

    if (!text || !*text) {
        snprintf(err, errsz, "控件 %s: show_message 缺 text", wid);
        return -1;
    }
    if (s_nactions >= WIDGET_MAX_PER_PAGE) {
        snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
        return -1;
    }
    pa = &s_actions[s_nactions];
    memset(pa, 0, sizeof(*pa));
    pa->kind = ACT_MESSAGE;
    pa->from_tag = -1;
    snprintf(pa->text, sizeof(pa->text), "%s", text);
    s_nactions++;
    return (int)s_nactions;
}

/* 解析一个事件的 actions 数组；支持的 do 见 37 §6.7 的 v1 子集。
 * 返回值：0 = 没有动作（空数组）；>0 = 登记的动作下标 + 1；-1 = 失败。 */
int parse_actions(const hmi_json_t* ev, const char* wid,
                  char* err, size_t errsz)
{
    const hmi_json_t* acts = hmi_json_get(ev, "actions");
    size_t n;

    /* script 分支（37 §6.2）：事件可以直接挂一段 Lua，与 actions 二选一 */
    {
        const char* src = hmi_json_str(hmi_json_get(ev, "script"), NULL);

        if (src) {
            page_action_t* pa;

            if (hmi_json_type(acts) == HMI_JSON_ARR) {
                snprintf(err, errsz, "控件 %s: 事件不能同时给 script 与 actions", wid);
                return -1;
            }
            if (!*src) {
                snprintf(err, errsz, "控件 %s: 事件的 script 为空", wid);
                return -1;
            }
            if (strlen(src) >= sizeof(pa->text)) {
                snprintf(err, errsz, "控件 %s: 事件 script 超过 %d 字节",
                         wid, (int)sizeof(pa->text));
                return -1;
            }
            if (s_nactions >= WIDGET_MAX_PER_PAGE) {
                snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
                return -1;
            }
            pa = &s_actions[s_nactions];
            memset(pa, 0, sizeof(*pa));
            pa->kind = ACT_LUA;
            pa->from_tag = -1;
            snprintf(pa->text, sizeof(pa->text), "%s", src);
            s_nactions++;
            return (int)s_nactions;
        }
    }
    if (hmi_json_type(acts) != HMI_JSON_ARR) {
        snprintf(err, errsz, "控件 %s: 事件既无 actions 数组也无 script", wid);
        return -1;
    }
    n = hmi_json_count(acts);
    if (n == 0) return 0;
    if (n > 1) {
        snprintf(err, errsz, "控件 %s: 单个事件暂只支持 1 个动作（v1）", wid);
        return -1;
    }

    {
        const hmi_json_t* a = hmi_json_at(acts, 0);
        const char* do_s = hmi_json_str(hmi_json_get(a, "do"), NULL);

        if (do_s && (strcmp(do_s, "goto_page") == 0 || strcmp(do_s, "open_page") == 0 ||
                     strcmp(do_s, "close_page") == 0 || strcmp(do_s, "back") == 0))
            return parse_page_action(a, do_s, wid, err, errsz);
        if (do_s && (strcmp(do_s, "toggle_tag") == 0 || strcmp(do_s, "add_var") == 0 ||
                     strcmp(do_s, "set_bit") == 0 || strcmp(do_s, "clear_bit") == 0))
            return parse_tag_op(a, do_s, wid, err, errsz);
        if (do_s && strcmp(do_s, "show_message") == 0)
            return parse_message(a, wid, err, errsz);
        if (do_s && strcmp(do_s, "save_now") == 0) {
            const char* grp = hmi_json_str(hmi_json_get(a, "group"), "");

            if (s_nactions >= WIDGET_MAX_PER_PAGE) {
                snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
                return -1;
            }
            s_actions[s_nactions].kind = ACT_SAVE_NOW;
            snprintf(s_actions[s_nactions].text, sizeof(s_actions[s_nactions].text),
                     "%s", grp ? grp : "");
            s_nactions++;
            return (int)s_nactions;
        }
        if (do_s && strcmp(do_s, "export_datalog") == 0) {
            /* {do:"export_datalog", group:"存盘组1", last_min:60} 或 from_ts/to_ts（unix 秒） */
            const char* grp = hmi_json_str(hmi_json_get(a, "group"), "");
            long lm = hmi_json_int(hmi_json_get(a, "last_min"), 0);
            long ft = hmi_json_int(hmi_json_get(a, "from_ts"), 0);
            long tt = hmi_json_int(hmi_json_get(a, "to_ts"), 0);

            if (!grp || !*grp) {
                snprintf(err, errsz, "控件 %s: export_datalog 需要 group", wid);
                return -1;
            }
            if (lm < 0 || lm > 10080) {
                snprintf(err, errsz, "控件 %s: export_datalog 的 last_min=%ld 越界（0..10080）",
                         wid, lm);
                return -1;
            }
            if (ft < 0 || tt < 0) {
                snprintf(err, errsz, "控件 %s: export_datalog 的 from_ts/to_ts 不能为负", wid);
                return -1;
            }
            if (s_nactions >= WIDGET_MAX_PER_PAGE) {
                snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
                return -1;
            }
            s_actions[s_nactions].kind = ACT_EXPORT_DATALOG;
            snprintf(s_actions[s_nactions].text, sizeof(s_actions[s_nactions].text),
                     "%s|%ld|%ld|%ld", grp, lm, ft, tt);
            s_nactions++;
            return (int)s_nactions;
        }
        if (do_s && strcmp(do_s, "set_language") == 0) {
            const char* lang = hmi_json_str(hmi_json_get(a, "lang"), NULL);

            if (!lang || !*lang) {
                snprintf(err, errsz, "控件 %s: set_language 缺 lang", wid);
                return -1;
            }
            if (s_nactions >= WIDGET_MAX_PER_PAGE) {
                snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
                return -1;
            }
            s_actions[s_nactions].kind = ACT_SET_LANGUAGE;
            snprintf(s_actions[s_nactions].text, sizeof(s_actions[s_nactions].text),
                     "%s", lang);
            s_nactions++;
            return (int)s_nactions;
        }
        if (do_s && (strcmp(do_s, "login") == 0 || strcmp(do_s, "logout") == 0)) {
            if (s_nactions >= WIDGET_MAX_PER_PAGE) {
                snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
                return -1;
            }
            /* 按整串判断：login 与 logout 首字母都是 l */
            s_actions[s_nactions].kind = (strcmp(do_s, "login") == 0) ? ACT_LOGIN
                                                                     : ACT_LOGOUT;
            s_nactions++;
            return (int)s_nactions;
        }
        if (do_s && (strcmp(do_s, "export_recipes") == 0 ||
                     strcmp(do_s, "import_recipes") == 0)) {
            if (s_nactions >= WIDGET_MAX_PER_PAGE) {
                snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
                return -1;
            }
            s_actions[s_nactions].kind = (do_s[1] == 'x') ? ACT_EXPORT_RECIPES
                                                          : ACT_IMPORT_RECIPES;
            s_actions[s_nactions].text[0] = '\0';
            s_nactions++;
            return (int)s_nactions;
        }
        if (do_s && (strcmp(do_s, "load_recipe") == 0 ||
                     strcmp(do_s, "save_recipe") == 0 ||
                     strcmp(do_s, "new_recipe") == 0)) {
            const char* grp = hmi_json_str(hmi_json_get(a, "group"), "");
            const char* nm = hmi_json_str(hmi_json_get(a, "name"), "");

            if (!grp || !*grp) {
                snprintf(err, errsz, "控件 %s: %s 需要 group", wid, do_s);
                return -1;
            }
            if (s_nactions >= WIDGET_MAX_PER_PAGE) {
                snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
                return -1;
            }
            if (do_s[0] == 'l') s_actions[s_nactions].kind = ACT_LOAD_RECIPE;
            else if (do_s[0] == 's') s_actions[s_nactions].kind = ACT_SAVE_RECIPE;
            else s_actions[s_nactions].kind = ACT_NEW_RECIPE;
            snprintf(s_actions[s_nactions].text, sizeof(s_actions[s_nactions].text),
                     "%s|%s", grp, nm ? nm : "");
            s_nactions++;
            return (int)s_nactions;
        }
        if (do_s && strcmp(do_s, "ack_alarm") == 0) {
            /* {do:"ack_alarm", alarm:3} 或 {do:"ack_alarm", group:"温度"}（省略 = 全部） */
            long al = hmi_json_int(hmi_json_get(a, "alarm"), -1);
            const char* grp = hmi_json_str(hmi_json_get(a, "group"), "");

            if (s_nactions >= WIDGET_MAX_PER_PAGE) {
                snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
                return -1;
            }
            s_actions[s_nactions].kind = ACT_ACK_ALARM;
            s_actions[s_nactions].tag_id = (int)al;
            snprintf(s_actions[s_nactions].text, sizeof(s_actions[s_nactions].text),
                     "%s", grp ? grp : "");
            s_nactions++;
            return (int)s_nactions;
        }
        if (do_s && strcmp(do_s, "shelve_alarm") == 0) {
            /* {do:"shelve_alarm", alarm:3, minutes:30}（alarm 省略 = 全部） */
            long al = hmi_json_int(hmi_json_get(a, "alarm"), -1);
            long mins = hmi_json_int(hmi_json_get(a, "minutes"), 30);

            if (mins < 1 || mins > 480) {
                snprintf(err, errsz, "控件 %s: shelve_alarm 的 minutes=%ld 越界（1..480）",
                         wid, mins);
                return -1;
            }
            if (s_nactions >= WIDGET_MAX_PER_PAGE) {
                snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
                return -1;
            }
            s_actions[s_nactions].kind = ACT_SHELVE_ALARM;
            s_actions[s_nactions].tag_id = (int)al;
            s_actions[s_nactions].value_num = (double)mins;
            s_nactions++;
            return (int)s_nactions;
        }
        if (do_s && strcmp(do_s, "write_device") == 0)
            return parse_device_action(a, wid, err, errsz);
        if (!do_s || strcmp(do_s, "write_tag") != 0) {
            snprintf(err, errsz,
                     "控件 %s: 动作 %s 暂未实现（v1 支持 write_tag / write_device / "
                     "toggle_tag / add_var / set_bit / clear_bit / 切页 / show_message / "
                     "ack_alarm / shelve_alarm / load_recipe / save_recipe / new_recipe）"
                     "；要跑 Lua 请写在事件上（\"events\":{\"click\":{\"script\":\"...\"}}）",
                     wid, do_s ? do_s : "(缺失)");
            return -1;
        }
        {
            long tag = hmi_json_int(hmi_json_get(a, "tag"), -1);
            const hmi_json_t* val = hmi_json_get(a, "value");

            if (tag < 0 || tag >= s_proj->tags_count) {
                /* 系统变量（60000+）是只读的：建页期就拒，不留到运行期才发现 */
                if (sysvar_index((int)tag) >= 0) {
                    snprintf(err, errsz,
                             "控件 %s: tag %ld 是系统变量（只读），不能作为写入目标",
                             wid, tag);
                } else {
                    snprintf(err, errsz, "控件 %s: 动作写的 tag %ld 不存在", wid, tag);
                }
                return -1;
            }
            if (s_nactions >= WIDGET_MAX_PER_PAGE) {
                snprintf(err, errsz, "控件 %s: 动作数超过每页上限", wid);
                return -1;
            }
            s_actions[s_nactions].kind = ACT_TAG;
            s_actions[s_nactions].tag_id = (int)tag;
            if (hmi_json_type(val) == HMI_JSON_BOOL) {
                s_actions[s_nactions].value_is_bool = 1;
                s_actions[s_nactions].value_bool = hmi_json_bool(val, 0);
            } else if (hmi_json_type(val) == HMI_JSON_NUM) {
                s_actions[s_nactions].value_is_bool = 0;
                s_actions[s_nactions].value_num = hmi_json_num(val, 0);
            } else {
                snprintf(err, errsz, "控件 %s: write_tag 的 value 只能是布尔或数值", wid);
                return -1;
            }
            {
                int slot = (int)s_nactions;
                s_nactions++;
                return slot + 1;   /* 0 保留给"无动作" */
            }
        }
    }
}

int page_mgr_area_from_name(const char* name)
{
    return area_from_name(name);
}

int page_mgr_dt_from_name(const char* name)
{
    return dt_from_name(name);
}

int page_mgr_write_device(const char* dev_id, int area, int addr, int datatype,
                          double num, int is_bool)
{
    const device_def_t* d;
    int di = -1;

    if (!s_proj || !dev_id) return -1;
    if (!auth_perm(PERM_DEBUG)) {
        HMI_LOG("auth: 拒绝直接写设备 %s（用户 %s，缺少 debug 权限）", dev_id,
                auth_user_name() ? auth_user_name() : "未登录");
        audit_denied("write_device", "debug");
        return -1;
    }
    for (int i = 0; i < s_proj->devices_count; i++)
        if (strcmp(s_proj->devices[i].id, dev_id) == 0) { di = i; break; }
    if (di < 0) return -1;
    d = &s_proj->devices[di];
    if (area != AREA_COIL && area != AREA_HOLDING) return -1;

    if (strcmp(d->protocol, "sim") == 0)
        return sim_write(s_sim, area, addr, datatype, num, is_bool);
    return modbus_write_device(s_proj, d->id, area, addr, datatype, num, is_bool);
}

int page_mgr_write_tag(int tag_id, int is_bool, int b, double n)
{
    if (!s_proj || tag_id < 0 || tag_id >= s_proj->tags_count) return -1;
    /* 脚本写变量与界面动作同一道闸（ADR-0012 决定 5 / ADR-0026）：
     * 当前页 operate 组掩码与命名 operate 位，两道都过才写 */
    if (!auth_can(s_page_oper) || !auth_perm(PERM_OPERATE)) {
        HMI_LOG("auth: 拒绝脚本写变量 %d（用户 %s，%s）", tag_id,
                auth_user_name() ? auth_user_name() : "未登录",
                auth_can(s_page_oper) ? "缺少 operate 权限"
                                      : "当前页 operate 组掩码不含该用户");
        audit_denied("cg.setTag", auth_can(s_page_oper) ? "operate" : "页面 perm 组掩码");
        return -1;
    }
    return apply_tag_write(tag_id, is_bool, b, n);
}
