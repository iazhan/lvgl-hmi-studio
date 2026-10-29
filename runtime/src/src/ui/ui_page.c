/*
 * ui_page.c — 页面生命周期：建页、拆页、页面栈、绑定刷新、文本占位符、页面脚本
 *
 * 动作/事件在 ui_page_actions.c，控件交互回调与提示条在 ui_page_input.c；
 * 三者共用页面级状态（控件数组、动作数组、工程上下文），声明在 ui_page_internal.h。
 */
#include "ui_page.h"
#include "ui_page_internal.h"
#include "widgets.h"
#include "widgets_internal.h"
#include "core/caps.h"
#include "core/sysvar.h"
#include "comm/modbus.h"
#include "script/lua_rt.h"
#include "core/tagdb.h"
#include "core/tagwrite.h"
#include "core/alarm.h"
#include "core/recipe.h"
#include "data/savecfg.h"
#include "hmi_usb.h"          /* 配方导入导出要挂 U 盘（hmi_usb_mount/umount） */
#include "ui_font.h"
#include "ui_image_bin.h"     /* 页面背景图（bg_image）也用同一套 .bin 校验 */
#include "ui_anim.h"
#include "ui_keypad.h"
#include "ui_login.h"
#include "core/audit.h"
#include "core/auth.h"
#include "core/i18n.h"
#include "widget_alarm.h"
#include "widget_chart.h"
#include "widget_recipe.h"
#include "hmi_board.h"
#include "hmi_json.h"
#include "hmi_log.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define REFRESH_MS 100        /* 绑定刷新周期（34 §5.7：50~100ms） */

/* 工程与页面上下文（另两个文件通过 ui_page_internal.h 共用前四个） */
const project_t* s_proj;
sim_dev_t*       s_sim;
widget_t         s_widgets[WIDGET_MAX_PER_PAGE];
int              s_nwidgets;
page_action_t    s_actions[WIDGET_MAX_PER_PAGE];
int              s_nactions;

static lv_obj_t* s_screen;
static lv_obj_t* s_default_screen;   /* 建页前的活动屏，关页时切回 */
static lv_timer_t* s_refresh_timer;

static const hmi_json_t* s_wnodes[WIDGET_MAX_PER_PAGE];   /* 压平后的控件 JSON（含子控件） */
static int       s_wparents[WIDGET_MAX_PER_PAGE];        /* 每个控件的父下标，-1=挂屏幕 */
static int       s_wtab[WIDGET_MAX_PER_PAGE];            /* tabview 子控件的页签号，-1=不在页签里 */
static int       s_nflat;

/* 控件 id 的副本：页面 JSON 在 build_page 末尾就释放了，s_wnodes 随之悬空；
 * 几何补丁（设计画布拖动）要按 id 找控件，所以建页时把 id 拷出来一次。
 * 150 × 32B = 4.8KB，板端也编 —— 省这点内存不值得让两种宿主走两套代码。 */
static char      s_wids[WIDGET_MAX_PER_PAGE][32];

/* 页面图层（ADR-0021）：图层页 JSON 与"槽属于哪一层"的映射 */
#define LAYER_MAX 4

static hmi_json_t* s_layers[LAYER_MAX];
static int         s_nlayers;
static char        s_layer_ids[LAYER_MAX][64];
static int         s_wlayer[WIDGET_MAX_PER_PAGE];   /* 槽属于哪一层，-1 = 本页 */

static void layers_free(void);               /* 定义在 build_page 之前，先声明 */
static void bg_image_free(void);      /* 定义在 build_page 之前，这里先声明 */
static binding_t* s_bindings;
static int       s_nbindings;

/* 页面栈（ADR 0002）：只存页面 id，只有栈顶那一页的对象存在，切页销毁旧页。
 * 动作在 LVGL 事件回调里发起，实际切换推迟到刷新定时器里做——不能在事件回调
 * 里删掉自己所在的控件树。 */
#define PAGE_STACK_MAX 8
static char s_stack[PAGE_STACK_MAX][64];
static int  s_depth;
static int  s_pending_op;
static char s_pending_page[64];
/* 推迟到刷新定时器的整页重建（切语言）：在控件自己的事件里拆整页，LVGL 还拿着正在派发的
 * 对象；重建后补一条提示（提示条属于页面，重建前弹会被一起拆掉） */
static int  s_pending_reload;
static char s_pending_toast[96];

/* 页面脚本（37 §6.x）：on_open 建页后、on_close 拆页前各跑一次，
 * on_timer 按 timer_ms 周期跑。源码用 strdup 存，拆页时释放。 */
static char* s_on_close;
static char* s_on_timer;
static lv_timer_t* s_script_timer;
static int   s_on_timer_fails;      /* on_timer 连续失败次数，3 次后停表 */

/* ── 权限（ADR-0012）──
 * 建控件时按 perm 定初值：看不见 → 隐藏；看得见不能操作 → 禁用（点击穿透不了）。
 * 动作执行前再判一次 operate，避免"看着点不动其实能绕过"。 */
perm_mask_t s_page_view;
perm_mask_t s_page_oper;

widget_t* widget_by_obj(lv_obj_t* obj);

/* 控件自身需要的命名位（ADR-0026）：查看历史类面板缺位就隐藏——页面还能进，
 * 现场看到的是"没有历史表"，日志里点名控件与缺的位。 */
static perm_bits_t widget_perm_bit(const widget_t* w, const hmi_json_t* node)
{
    if (w->type == W_HISTORY_TABLE) return PERM_VIEW_HISTORY;
    if (w->type == W_ALARM_LIST) {
        const char* mode = hmi_json_str(
            hmi_json_get(hmi_json_get(node, "props"), "mode"), "active");

        if (mode && strcmp(mode, "history") == 0) return PERM_VIEW_HISTORY;
    }
    return 0;
}

static int apply_perm(widget_t* w, const hmi_json_t* node, const char* wid,
                      char* err, size_t errsz)
{
    perm_mask_t view = s_page_view;
    perm_mask_t op = s_page_oper;
    const hmi_json_t* p = hmi_json_get(node, "perm");

    if (p && perm_parse(p, &view, &op, wid, err, errsz) != 0) return -1;
    w->pview = view;
    w->poper = op;
    if (!w->obj) return 0;
    if (!auth_can(view)) {
        lv_obj_add_flag(w->obj, LV_OBJ_FLAG_HIDDEN);
        return 0;
    }
    {
        perm_bits_t need = widget_perm_bit(w, node);

        if (need && !auth_perm(need)) {
            HMI_LOG("auth: 控件 %s 需要 %s 权限，已隐藏", wid, perm_bit_name(need));
            lv_obj_add_flag(w->obj, LV_OBJ_FLAG_HIDDEN);
            return 0;
        }
    }
    if (!auth_can(op)) {
        /* 表格类控件不禁用：DISABLED 去掉 CLICKABLE 后连滚动都点不动，
         * 现场会以为界面卡了。它们的"不可改"由控件自己在编辑/下载时校验并提示。 */
        if (w->type == W_RECIPE_TABLE || w->type == W_HISTORY_TABLE ||
            w->type == W_ALARM_LIST) {
            lv_obj_set_style_opa(w->obj, LV_OPA_70, 0);   /* 略微压暗，提示"只能看" */
            return 0;
        }
        lv_obj_remove_flag(w->obj, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_add_state(w->obj, LV_STATE_DISABLED);
        lv_obj_set_style_opa(w->obj, LV_OPA_50, 0);
    }
    return 0;
}

/* ── 含 {tag:...} 的文本：定时重算（ADR-0013）──
 * 文本随变量变化，建页时渲染一次不够；页面级 500ms 定时器统一重算，
 * 只登记真正带占位符的控件（上限 32 个），其余不动。 */
#define PH_MAX 32

/* 源码与渲染结果都按需分配：label.text 上限 512 字符（中文 1500+ 字节），
 * 以前 src 只有 96 字节，长文本被截在占位符中间——`{tag:60010|%s|未登录}` 掉了 `}`，
 * 于是原样显示在屏上（实测 about 页就是这样）。 */
static struct {
    lv_obj_t* obj;
    char*     src;
    char*     out;
    size_t    outsz;
} s_ph[PH_MAX];

static int         s_nph;

static lv_timer_t* s_ph_timer;

static void ph_tick_cb(lv_timer_t* t)
{
    static unsigned n;
    static int trace = -1;

    (void)t;
    if (trace < 0) {
        const char* e = getenv("HMI_RT_TRACE");

        trace = (e && *e && *e != '0') ? 1 : 0;
    }
    for (int i = 0; i < s_nph; i++) {
        if (!s_ph[i].obj || !s_ph[i].out) continue;
        i18n_render(s_ph[i].src, s_ph[i].out, s_ph[i].outsz);
        /* 文字没变就别 set：省一次排版，也让下面的日志只在"真的跟着变了"时出现 */
        if (strcmp(lv_label_get_text(s_ph[i].obj), s_ph[i].out) == 0) continue;
        lv_label_set_text(s_ph[i].obj, s_ph[i].out);
        n++;
        if (trace) HMI_LOG("占位符刷新 #%u：%s", n, s_ph[i].out);
    }
}

static void ph_register(lv_obj_t* obj, const char* src)
{
    lv_obj_t* tgt = obj;

    if (!obj || !src || s_nph >= PH_MAX) return;
    if (!lv_obj_check_type(tgt, &lv_label_class)) {
        uint32_t n = lv_obj_get_child_count(tgt);

        for (uint32_t k = 0; k < n; k++) {
            lv_obj_t* ch = lv_obj_get_child(tgt, (int32_t)k);

            if (ch && lv_obj_check_type(ch, &lv_label_class)) { tgt = ch; break; }
        }
        if (tgt == obj) return;               /* 找不到文字标签（输入框这类） */
    }
    {
        size_t n = strlen(src);
        char* s = (char*)malloc(n + 1);
        size_t osz = n * 2 + 160;
        char* o = (char*)malloc(osz);

        if (!s || !o) {
            free(s);
            free(o);
            HMI_LOG("page: 占位符登记失败（内存不足）");
            return;
        }
        memcpy(s, src, n + 1);
        s_ph[s_nph].obj = tgt;
        s_ph[s_nph].src = s;
        s_ph[s_nph].out = o;
        s_ph[s_nph].outsz = osz;
        s_nph++;
    }
    /* 初值也打一行：后面只在"文字真的变了"时打日志，初值不打就没法从日志看未登录态 */
    {
        const char* e = getenv("HMI_RT_TRACE");

        if (e && *e && *e != '0') {
            i18n_render(s_ph[s_nph - 1].src, s_ph[s_nph - 1].out, s_ph[s_nph - 1].outsz);
            HMI_LOG("占位符初值 #%d：%s", s_nph, s_ph[s_nph - 1].out);
        }
    }
}

/* 按对象找控件：长按判断要知道按下的时刻 */
widget_t* widget_by_obj(lv_obj_t* obj)
{
    for (int i = 0; i < s_nwidgets; i++)
        if (s_widgets[i].obj == obj) return &s_widgets[i];
    return NULL;
}

static void script_timer_cb(lv_timer_t* t)
{
    char err[256];

    LV_UNUSED(t);
    if (!s_on_timer) return;
    if (lua_rt_dostring(s_on_timer, "=<page>.on_timer", err, sizeof(err)) == 0) {
        s_on_timer_fails = 0;
        return;
    }
    /* 37 §7.3：同一脚本连续报错 3 次就停用，别让它每 100ms 刷一行错误把 tmpfs 写满 */
    if (++s_on_timer_fails >= 3) {
        HMI_LOG("page: on_timer 连续失败 3 次，停用本页定时脚本（最后错误：%s）", err);
        lv_timer_delete(t);
        s_script_timer = NULL;
    } else {
        HMI_LOG("page: on_timer 失败（%d/3）：%s", s_on_timer_fails, err);
    }
}

/* 把页面控件树压平：父在前、子在后，并记下每个控件的父节点下标（-1 = 挂屏幕）。
 * 这样建控件、建绑定、挂事件三个循环都能按同一个下标走，不必各自递归。 */
/* 键盘控件（W_KEYBOARD）：建完所有控件才知道 target 是谁，这里按 id 找到它，
 * 再把内联键盘挂到键盘控件的容器里。一页只允许一个（键盘状态是单例）。 */
static int link_keyboards(const char* page_id, const hmi_json_t* const* nodes,
                          widget_t* ws, int n, char* err, size_t errsz)
{
    for (int i = 0; i < n; i++) {
        const hmi_json_t* props;
        const char* tid;
        const char* wid;
        int tgt = -1;

        if (ws[i].type != W_KEYBOARD) continue;
        props = hmi_json_get(nodes[i], "props");
        wid = hmi_json_str(hmi_json_get(nodes[i], "id"), "?");
        tid = hmi_json_str(hmi_json_get(props, "target"), NULL);
        if (!tid || !*tid) {
            snprintf(err, errsz, "pages/%s.json: 键盘 %s 缺 props.target", page_id, wid);
            return -1;
        }
        /* target 支持两种写法：裸 id（先找本页，再找图层）与 "图层.id" */
        {
            const char* dot = strchr(tid, '.');

            for (int k = 0; k < n; k++) {
                const char* id = hmi_json_str(hmi_json_get(nodes[k], "id"), NULL);

                if (!id) continue;
                if (dot) {
                    int li = (int)(dot - tid);

                    if ((int)strlen(id) == (int)strlen(dot + 1) &&
                        strncmp(id, dot + 1, strlen(dot + 1)) == 0 &&
                        s_wlayer[k] >= 0 &&
                        strncmp(s_layer_ids[s_wlayer[k]], tid, (size_t)li) == 0 &&
                        s_layer_ids[s_wlayer[k]][li] == '\0') {
                        tgt = k;
                        break;
                    }
                } else if (strcmp(id, tid) == 0) {
                    if (s_wlayer[k] < 0) { tgt = k; break; }   /* 本页优先 */
                    if (tgt < 0) tgt = k;                      /* 退而求其次：图层 */
                }
            }
        }
        if (tgt < 0) {
            snprintf(err, errsz, "pages/%s.json: 键盘 %s 的 target %s 不存在",
                     page_id, wid, tid);
            return -1;
        }
        if (keyboard_attach(&ws[i], &ws[tgt], err, errsz) != 0) return -1;
    }
    return 0;
}

static int flatten_widgets(const hmi_json_t* arr, int parent_slot,
                           const hmi_json_t** nodes, int* parents, int* tabs,
                           int cur_tab, int* n, char* err, size_t errsz)
{
    size_t cnt = hmi_json_count(arr);

    for (size_t i = 0; i < cnt; i++) {
        const hmi_json_t* wj = hmi_json_at(arr, i);
        const hmi_json_t* props;
        const hmi_json_t* kids;
        int slot;

        if (*n >= WIDGET_MAX_PER_PAGE) {
            snprintf(err, errsz,
                     "控件数超过每页上限 %d（含容器子控件、页签内容与模板实例展开后的控件）",
                     WIDGET_MAX_PER_PAGE);
            return -1;
        }
        slot = (*n)++;
        nodes[slot] = wj;
        parents[slot] = parent_slot;
        tabs[slot] = cur_tab;

        props = hmi_json_get(wj, "props");
        /* 容器子控件：继承父的页签号（页签里的容器，其子控件也在同一页签） */
        kids = hmi_json_get(props, "children");
        if (hmi_json_type(kids) == HMI_JSON_ARR &&
            flatten_widgets(kids, slot, nodes, parents, tabs, cur_tab,
                            n, err, errsz) != 0)
            return -1;

        /* tabview 的页签内容：父仍是 tabview，但要记住属于第几个页签，
         * 建对象时挂到那一页上（见建页循环）。 */
        {
            const hmi_json_t* tarr = hmi_json_get(props, "tabs");

            if (hmi_json_type(tarr) == HMI_JSON_ARR) {
                for (size_t k = 0; k < hmi_json_count(tarr); k++) {
                    const hmi_json_t* tw =
                        hmi_json_get(hmi_json_at(tarr, k), "widgets");

                    if (hmi_json_type(tw) != HMI_JSON_ARR) continue;
                    if (flatten_widgets(tw, slot, nodes, parents, tabs, (int)k,
                                        n, err, errsz) != 0)
                        return -1;
                }
            }
        }
    }
    return 0;
}

static void page_switch_apply(void);

static void refresh_timer_cb(lv_timer_t* t)
{
    LV_UNUSED(t);
    if (s_pending_op) {
        page_switch_apply();
    } else if (s_pending_reload) {
        char toast[96];

        s_pending_reload = 0;
        snprintf(toast, sizeof(toast), "%s", s_pending_toast);
        s_pending_toast[0] = '\0';
        (void)page_mgr_reload();
        if (toast[0]) toast_show(toast);
    }
    bind_table_refresh(s_bindings, s_nbindings);
    ui_anim_tick();                /* 表达式触发的沿检测 */
    for (int i = 0; i < s_nwidgets; i++)
        if (s_widgets[i].nwstates) (void)ui_widget_states_apply(&s_widgets[i]);
}

/* ── 打开 / 关闭 ── */

/* 拆掉当前页的对象与表，但不碰页面栈、不切回默认屏（切页时新旧交替用）。
 * s_proj / s_sim 保留：切页过程中还要用它们建下一页。 */
static void page_teardown(void)
{
    /* 页面脚本收尾：on_close 在拆控件之前跑（此时 cg 与变量表都还在），
     * 然后停掉 on_timer 的定时器、释放源码副本。 */
    if (s_on_close) {
        char err[256];

        if (lua_rt_dostring(s_on_close, "=<page>.on_close", err, sizeof(err)) != 0)
            HMI_LOG("page: on_close 失败：%s", err);
    }
    if (s_script_timer) {
        lv_timer_delete(s_script_timer);
        s_script_timer = NULL;
    }
    free(s_on_timer);
    free(s_on_close);
    s_on_timer = NULL;
    s_on_close = NULL;

    layers_free();            /* 失败路径兜底（成功路径已释放，这里幂等） */
    ui_anim_reset();          /* 停掉动画并还原属性（对象还在，先还再删屏） */
    ui_keypad_reset();
    login_dialog_reset();
    if (s_ph_timer) {
        lv_timer_delete(s_ph_timer);
        s_ph_timer = NULL;
    }
    for (int i = 0; i < s_nph; i++) {          /* 占位符的源码/渲染缓冲是堆上按需分配的 */
        free(s_ph[i].src);
        free(s_ph[i].out);
        s_ph[i].src = NULL;
        s_ph[i].out = NULL;
        s_ph[i].obj = NULL;
    }
    s_nph = 0;
    toast_reset();
    confirm_reset();          /* 确认框是模态浮层，拆页时连同定时器一起收 */
    if (s_refresh_timer) {
        lv_timer_delete(s_refresh_timer);
        s_refresh_timer = NULL;
    }
    if (s_screen) {
        /* 删活动屏会让 LVGL 拿着悬垂指针（lv_obj_delete 会报 "active screen was deleted"） */
        if (s_default_screen && lv_screen_active() == s_screen)
            lv_screen_load(s_default_screen);
        lv_obj_delete(s_screen);
        s_screen = NULL;
    }
    bg_image_free();          /* 背景图像素缓冲：对象已随 screen 删掉，这里放数据 */
    bind_table_free(s_bindings, s_nbindings);
    s_bindings = NULL;
    s_nbindings = 0;
    ui_children_free_images();
    /* 每个控件的堆内存与定时器按类型释放（族字段在 union 里，不能一刀切） */
    for (int i = 0; i < s_nwidgets; i++) widget_dispose(&s_widgets[i]);
    memset(s_widgets, 0, sizeof(s_widgets));
    s_nwidgets = 0;
    memset(s_actions, 0, sizeof(s_actions));
    s_nactions = 0;
}

/* 建一页并加载：成功时 s_screen 指向新屏。失败时清掉半成品，返回 -1。 */
/* ── 页面图层（ADR-0021）──
 * layers[] 引用的页面先压平、先建（先建 = 在下面）。图层控件与本页控件共用同一套
 * 压平数组，所以 150 上限天然按合页后算。图层页必须是纯版式：禁脚本、禁 perm、禁嵌套。
 * 控件 id 对外表现成 <图层>. <id>：去重按"同层内唯一"判，引用时支持 "hdr.w1" 写法。 */
static void layers_free(void)
{
    for (int i = 0; i < LAYER_MAX; i++) {
        if (s_layers[i]) {
            hmi_json_free(s_layers[i]);
            s_layers[i] = NULL;
        }
        s_layer_ids[i][0] = '\0';
    }
    s_nlayers = 0;
}

static int layer_load(const project_t* proj, const char* layer_id, int idx,
                      char* err, size_t errsz)
{
    char path[300], jerr[160];
    size_t len = 0;
    char* text;
    hmi_json_t* p;
    const char* f;

    if (project_page_path(proj, layer_id, path, sizeof(path)) != 0) {
        snprintf(err, errsz, "图层页 %s: 路径过长", layer_id);
        return -1;
    }
    text = loader_read_text(path, 256 * 1024, &len);
    if (!text) {
        snprintf(err, errsz, "图层页 %s 打不开（%s）", layer_id, path);
        return -1;
    }
    p = hmi_json_parse(text, len, jerr, sizeof(jerr));
    free(text);
    if (!p) {
        snprintf(err, errsz, "图层页 %s: 解析失败：%s", layer_id, jerr);
        return -1;
    }
    if (hmi_json_type(p) != HMI_JSON_OBJ) {
        snprintf(err, errsz, "图层页 %s: 根节点不是对象", layer_id);
        hmi_json_free(p);
        return -1;
    }
    /* 纯版式三条：脚本、权限、嵌套一律拒绝（ADR-0021） */
    f = hmi_json_str(hmi_json_get(p, "on_open"), NULL);
    if (f && *f) goto has_script;
    f = hmi_json_str(hmi_json_get(p, "on_timer"), NULL);
    if (f && *f) goto has_script;
    f = hmi_json_str(hmi_json_get(p, "on_close"), NULL);
    if (f && *f) goto has_script;
    if (hmi_json_get(p, "perm")) {
        snprintf(err, errsz, "图层页 %s: 不得带 perm（权限按合页后的页面算）", layer_id);
        hmi_json_free(p);
        return -1;
    }
    if (hmi_json_get(p, "layers")) {
        snprintf(err, errsz, "图层页 %s: 不得再引用图层（不支持嵌套）", layer_id);
        hmi_json_free(p);
        return -1;
    }
    s_layers[idx] = p;
    snprintf(s_layer_ids[idx], sizeof(s_layer_ids[idx]), "%s", layer_id);
    return 0;

has_script:
    snprintf(err, errsz, "图层页 %s: 不得带脚本（on_open/on_timer/on_close）", layer_id);
    hmi_json_free(p);
    return -1;
}

/* 把图层与本页的控件压进同一套数组：图层在前，本页在后 */
static int layers_prepare(const project_t* proj, const hmi_json_t* page,
                          const hmi_json_t* widgets_json, char* err, size_t errsz)
{
    const hmi_json_t* larr = hmi_json_get(page, "layers");
    int nflat = 0;

    layers_free();
    for (int i = 0; i < WIDGET_MAX_PER_PAGE; i++) s_wlayer[i] = -1;

    if (hmi_json_type(larr) == HMI_JSON_ARR) {
        size_t nl = hmi_json_count(larr);

        if (nl > LAYER_MAX) {
            snprintf(err, errsz, "页面 layers 有 %zu 个，超过上限 %d", nl, LAYER_MAX);
            return -1;
        }
        for (size_t k = 0; k < nl; k++) {
            const char* lid = hmi_json_str(hmi_json_at(larr, k), NULL);
            const hmi_json_t* lw;
            int before;

            if (!lid || !*lid) {
                snprintf(err, errsz, "layers[%zu] 不是有效的页面 id", k);
                return -1;
            }
            if (layer_load(proj, lid, (int)k, err, errsz) != 0) return -1;
            s_nlayers = (int)k + 1;
            lw = hmi_json_get(s_layers[k], "widgets");
            if (hmi_json_type(lw) != HMI_JSON_ARR) {
                snprintf(err, errsz, "图层页 %s: 缺 widgets 数组", lid);
                return -1;
            }
            before = nflat;
            if (flatten_widgets(lw, -1, s_wnodes, s_wparents, s_wtab, -1,
                                &nflat, err, errsz) != 0)
                return -1;
            for (int t = before; t < nflat; t++) s_wlayer[t] = (int)k;
        }
    }
    {
        int before = nflat;

        if (flatten_widgets(widgets_json, -1, s_wnodes, s_wparents, s_wtab, -1,
                            &nflat, err, errsz) != 0)
            return -1;
        for (int t = before; t < nflat; t++) s_wlayer[t] = -1;
    }
    s_nflat = nflat;

    /* id 去重：同层内必须唯一；跨层允许重复（对外表现成 <图层>.id） */
    for (int i = 0; i < s_nflat; i++) {
        const char* a = hmi_json_str(hmi_json_get(s_wnodes[i], "id"), NULL);

        if (!a) continue;
        for (int j = 0; j < i; j++) {
            const char* b = hmi_json_str(hmi_json_get(s_wnodes[j], "id"), NULL);

            if (b && strcmp(a, b) == 0 && s_wlayer[i] == s_wlayer[j]) {
                snprintf(err, errsz,
                         "控件 id 重复：%s（%s 内出现两次）", a,
                         s_wlayer[i] >= 0 ? s_layer_ids[s_wlayer[i]] : "本页");
                return -1;
            }
        }
    }
    return 0;
}

/* 页面背景图（ADR-0020）：bg_color 打底、图贴在上面。像素缓冲是页面级的，
 * 拆页时释放；加载失败只告警并退回纯色底——背景图坏了不该让整页打不开。 */
static lv_image_dsc_t s_bg_dsc;
static void*          s_bg_buf;

static void bg_image_free(void)
{
    if (s_bg_buf) {
        free(s_bg_buf);
        s_bg_buf = NULL;
    }
    memset(&s_bg_dsc, 0, sizeof(s_bg_dsc));
}

static void bg_image_apply(lv_obj_t* scr, const hmi_json_t* page, const char* page_id)
{
    const hmi_json_t* b = hmi_json_get(page, "bg_image");
    const char* src;
    const char* mode;
    char path[400], why[160];
    lv_image_align_t align = LV_IMAGE_ALIGN_STRETCH;
    int opa;
    lv_obj_t* o;

    bg_image_free();
    if (hmi_json_type(b) != HMI_JSON_OBJ) return;
    src = hmi_json_str(hmi_json_get(b, "src"), NULL);
    if (!src || !*src) {
        HMI_LOG("page %s: bg_image 缺 src，改用 bg_color", page_id);
        return;
    }
    if (image_bin_path(ui_asset_root, src, path, sizeof(path)) != 0) {
        HMI_LOG("page %s: bg_image.src %s 不是工程内相对路径，改用 bg_color", page_id, src);
        return;
    }
    if (image_bin_load(path, &s_bg_dsc, &s_bg_buf, why, sizeof(why)) != 0) {
        HMI_LOG("page %s: 背景图加载失败（%s），改用 bg_color", page_id, why);
        return;
    }
    mode = hmi_json_str(hmi_json_get(b, "mode"), "stretch");
    if (strcmp(mode, "tile") == 0) align = LV_IMAGE_ALIGN_TILE;
    else if (strcmp(mode, "center") == 0) align = LV_IMAGE_ALIGN_CENTER;
    else if (strcmp(mode, "stretch") != 0) {
        HMI_LOG("page %s: bg_image.mode %s 不认识（tile/stretch/center），按 stretch",
                page_id, mode);
    }
    opa = (int)hmi_json_int(hmi_json_get(b, "opacity"), 255);
    if (opa < 0) opa = 0;
    if (opa > 255) opa = 255;

    /* 直接挂在屏上：后建的控件自然盖在它上面 */
    o = lv_image_create(scr);
    if (!o) {
        bg_image_free();
        return;
    }
    lv_image_set_src(o, &s_bg_dsc);
    lv_obj_set_pos(o, 0, 0);
    lv_obj_set_size(o, HMI_LCD_WIDTH, HMI_LCD_HEIGHT);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_image_opa(o, (lv_opa_t)opa, 0);
    lv_image_set_inner_align(o, align);
}

/*
 * json_text 非空 = 用调用方给的页面 JSON 建页（设计画布推最新快照时走这条）；
 * 为空 = 从工程目录读 pages/<id>.json（板端与普通预览都走这条）。
 */
static int build_page(const project_t* proj, sim_dev_t* sim, const char* page_id,
                      const char* json_text, size_t json_len,
                      char* err, size_t errsz)
{
    char path[300], jerr[160];
    size_t len = 0;
    char* text;
    hmi_json_t* page;
    const hmi_json_t* widgets_json;
    const char* bg_s, *title_s;
    size_t nwidgets, i;

    if (json_text && json_len > 0) {
        page = hmi_json_parse(json_text, json_len, jerr, sizeof(jerr));
    } else {
        if (project_page_path(proj, page_id, path, sizeof(path)) != 0) {
            snprintf(err, errsz, "页面路径过长");
            return -1;
        }
        text = loader_read_text(path, 256 * 1024, &len);
        if (!text) {
            snprintf(err, errsz, "读页面文件失败（%s）", path);
            return -1;
        }
        page = hmi_json_parse(text, len, jerr, sizeof(jerr));
        free(text);
    }
    if (!page) {
        snprintf(err, errsz, "pages/%s.json: 解析失败：%s", page_id, jerr);
        return -1;
    }
    if (hmi_json_type(page) != HMI_JSON_OBJ) {
        snprintf(err, errsz, "pages/%s.json: 根节点不是对象", page_id);
    hmi_json_free(page);
        return -1;
    }
    {
        const char* pid = hmi_json_str(hmi_json_get(page, "id"), NULL);
        if (!pid || strcmp(pid, page_id) != 0) {
            snprintf(err, errsz, "pages/%s.json: id 与文件名不一致（%s）",
                     page_id, pid ? pid : "(缺失)");
            hmi_json_free(page);
            return -1;
        }
    }

    bg_s = hmi_json_str(hmi_json_get(page, "bg_color"), "#000000");
    if (!ui_color_valid(bg_s)) {
        snprintf(err, errsz, "pages/%s.json: bg %s 不是 #RRGGBB", page_id, bg_s);
        hmi_json_free(page);
        return -1;
    }

    widgets_json = hmi_json_get(page, "widgets");
    if (hmi_json_type(widgets_json) != HMI_JSON_ARR) {
        snprintf(err, errsz, "pages/%s.json: 缺 widgets 数组", page_id);
        hmi_json_free(page);
        return -1;
    }
    nwidgets = hmi_json_count(widgets_json);
    if (nwidgets > WIDGET_MAX_PER_PAGE) {
        snprintf(err, errsz,
                 "pages/%s.json: 控件数 %zu 超过每页上限 %d"
                 "（含容器子控件、页签内容与模板实例展开后的控件）",
                 page_id, nwidgets, WIDGET_MAX_PER_PAGE);
        hmi_json_free(page);
        return -1;
    }

    /* 建屏并加载：后续控件创建都落在活动屏上 */
    s_proj = proj;
    s_sim = sim;
    ui_set_asset_root(proj->root);
    ui_set_project(proj);
    sysvar_set_page(page_id);        /* $SYS$SCR 读它 */
    ui_children_reset();
    if (!s_default_screen) s_default_screen = lv_screen_active();
    s_screen = lv_obj_create(NULL);
    if (!s_screen) {
        snprintf(err, errsz, "pages/%s.json: 建屏失败（内存不足）", page_id);
        hmi_json_free(page);
        return -1;
    }
    ui_keypad_set_screen(s_screen);      /* 键盘挂在这一页上，随页销毁（ADR-0010） */
    lv_obj_set_size(s_screen, HMI_LCD_WIDTH, HMI_LCD_HEIGHT);
    lv_obj_set_pos(s_screen, 0, 0);
    lv_obj_set_style_pad_all(s_screen, 0, 0);
    lv_obj_set_style_border_width(s_screen, 0, 0);
    lv_obj_set_style_radius(s_screen, 0, 0);
    lv_obj_set_style_bg_color(s_screen, ui_color_hex(bg_s), 0);
    lv_obj_set_style_bg_opa(s_screen, LV_OPA_COVER, 0);
    lv_obj_set_scrollbar_mode(s_screen, LV_SCROLLBAR_MODE_OFF);
    lv_obj_clear_flag(s_screen, LV_OBJ_FLAG_SCROLLABLE);
    lv_screen_load(s_screen);
    bg_image_apply(s_screen, page, page_id);   /* 最先建 = 压在最底下 */

    /* 页面权限（ADR-0012）：view 不满足就不让进；operate 作为控件的缺省 */
    s_page_view = 0;
    s_page_oper = 0;
    if (hmi_json_get(page, "perm")) {
        if (perm_parse(hmi_json_get(page, "perm"), &s_page_view, &s_page_oper,
                       page_id, err, errsz) != 0) {
            hmi_json_free(page);
            page_teardown();
            return -1;
        }
        if (!auth_can(s_page_view)) {
            snprintf(err, errsz, "页面 %s 需要权限（当前%s）", page_id,
                     auth_logged_in() ? auth_user_name() : "未登录");
            hmi_json_free(page);
            page_teardown();
            return -1;
        }
    }

    /* 控件 */
    /* 压平：图层在前、本页在后（先建 = 在下面）。容器里的子控件与顶层控件
     * 一样参与绑定与事件；150 上限按合页后的总数算。 */
    if (layers_prepare(proj, page, widgets_json, err, errsz) != 0) {
        hmi_json_free(page);
        page_teardown();
        return -1;
    }
    for (i = 0; i < (size_t)s_nflat; i++) {
        lv_obj_t* par = (s_wparents[i] >= 0) ? s_widgets[s_wparents[i]].obj : NULL;

        /* 页签里的控件挂到对应的内容页上（tabview 先建，父下标必小于子下标） */
        if (s_wtab[i] >= 0 && s_wparents[i] >= 0 &&
            s_widgets[s_wparents[i]].type == W_TABVIEW &&
            s_wtab[i] < s_widgets[s_wparents[i]].u.tab.npages)
            par = s_widgets[s_wparents[i]].u.tab.pages[s_wtab[i]];

        /* 用了带 requires 的控件就必须在 manifest.features 里声明（37 §8 的
         * feature_not_declared）：漏声明时板上表现是"控件在、数据不动"，
         * 建页期挡住比让人猜便宜。 */
        {
            const char* wt = hmi_json_str(hmi_json_get(s_wnodes[i], "type"), NULL);
            const char* req = schema_widget_requires(wt);

            if (req) {
                int idx = caps_feature_index(req);
                if (idx < 0 || !(proj->manifest.features_mask & (1u << idx))) {
                    snprintf(err, errsz,
                             "pages/%s.json: 控件 %s 是 %s，需要 manifest.features 声明 %s",
                             page_id,
                             hmi_json_str(hmi_json_get(s_wnodes[i], "id"), "?"),
                             wt, req);
                    ui_set_parent(NULL);
                    hmi_json_free(page);
                    page_teardown();
                    return -1;
                }
            }
        }

        snprintf(s_wids[i], sizeof(s_wids[i]), "%s",
                 hmi_json_str(hmi_json_get(s_wnodes[i], "id"), ""));
        ui_set_parent(par);
        if (widget_build(s_wnodes[i], &s_widgets[i], err, errsz) != 0) {
            if (s_wlayer[i] >= 0) {          /* 报错点名是哪一层 */
                char inner[PROJ_ERR_MAX];

                snprintf(inner, sizeof(inner), "%s", err);
                snprintf(err, errsz, "图层 %s: %s", s_layer_ids[s_wlayer[i]], inner);
            }
            ui_set_parent(NULL);
            hmi_json_free(page);
            page_teardown();
            return -1;
        }
        ui_set_parent(NULL);
        s_nwidgets = (int)i + 1;
        {
            const char* tx = hmi_json_str(hmi_json_get(hmi_json_get(s_wnodes[i], "props"),
                                                       "text"), NULL);

            if (i18n_has_placeholder(tx)) ph_register(s_widgets[i].obj, tx);
        }
        /* frame_anim 的"开关变量"：bind.running 指向的变量为 0 时停播（见 widgets.c） */
        if (s_widgets[i].type == W_FRAME_ANIM) {
            const hmi_json_t* bnode = hmi_json_get(s_wnodes[i], "bind");
            const hmi_json_t* rn = bnode ? hmi_json_get(bnode, "running") : NULL;
            long tid = rn ? hmi_json_int(hmi_json_get(rn, "tag"), -1) : -1;

            if (tagdb_valid(tid)) s_widgets[i].gate_tag = (int)tid;
        }
        if (apply_perm(&s_widgets[i], s_wnodes[i],
                       hmi_json_str(hmi_json_get(s_wnodes[i], "id"), "?"),
                       err, errsz) != 0) {
            hmi_json_free(page);
            page_teardown();
            return -1;
        }
    }

    /* 绑定表 */
    if (bind_table_build(&s_bindings, &s_nbindings, s_wnodes,
                         s_widgets, s_nwidgets, err, errsz) != 0) {
        hmi_json_free(page);
        page_teardown();
        return -1;
    }

    /* tooltip：带 props.tooltip 的控件挂"按住 800ms 弹提示"的钩子。
     * 不可点的对象（label 之类）要补 CLICKABLE，否则收不到 PRESSED。 */
    for (i = 0; i < (size_t)s_nflat; i++) {
        const char* tip = hmi_json_str(
            hmi_json_get(hmi_json_get(s_wnodes[i], "props"), "tooltip"), NULL);

        if (!tip || !*tip || !s_widgets[i].obj) continue;
        snprintf(s_widgets[i].tip, sizeof(s_widgets[i].tip), "%s", tip);
        lv_obj_add_flag(s_widgets[i].obj, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_add_event_cb(s_widgets[i].obj, tip_begin_cb, LV_EVENT_PRESSED, &s_widgets[i]);
        lv_obj_add_event_cb(s_widgets[i].obj, tip_end_cb, LV_EVENT_RELEASED, &s_widgets[i]);
        lv_obj_add_event_cb(s_widgets[i].obj, tip_end_cb, LV_EVENT_PRESS_LOST, &s_widgets[i]);
        lv_obj_add_event_cb(s_widgets[i].obj, tip_end_cb, LV_EVENT_DELETE, &s_widgets[i]);
    }

    /* 页面用了 animations 就必须声明 anim 能力——与控件的 requires 同一条规则：
     * 能力只在固件侧查不够，工程漏声明时板上表现是"动画写了不动"。 */
    {
        const hmi_json_t* an = hmi_json_get(page, "animations");

        if (hmi_json_type(an) == HMI_JSON_ARR && hmi_json_count(an) > 0) {
            int ai = caps_feature_index("anim");

            if (ai < 0 || !(proj->manifest.features_mask & (1u << ai))) {
                snprintf(err, errsz,
                         "pages/%s.json: 有 %zu 条 animations，需要 manifest.features 声明 anim",
                         page_id, hmi_json_count(an));
                hmi_json_free(page);
                page_teardown();
                return -1;
            }
        }
    }

    /* 属性动画（46 §4）：建完控件才能按 id 找到 target */
    if (ui_anim_build(hmi_json_get(page, "animations"), s_widgets, s_wnodes, s_nflat,
                      err, errsz) != 0) {
        hmi_json_free(page);
        page_teardown();
        return -1;
    }

    /* 下拉框：选项来自变量的 enum，选中即写回（37 §6.4 combobox 的 change） */
    for (i = 0; i < (size_t)s_nwidgets; i++) {
        if (s_widgets[i].type != W_COMBOBOX || s_widgets[i].opt_tag < 0) continue;
        lv_obj_add_event_cb(s_widgets[i].obj, combo_change_cb,
                            LV_EVENT_VALUE_CHANGED, &s_widgets[i]);
    }

    /* 可写绑定的写回：开关拨动、滑块拖动直接改变量，灯/文字随之联动 */
    for (i = 0; i < (size_t)s_nbindings; i++) {
        binding_t* b = &s_bindings[i];

        if (!b->w || !b->w->obj) continue;
        if (b->prop == BIND_TEXT) b->w->bind_tag = b->tag_id;
        if (b->w->type == W_INPUT && b->prop == BIND_TEXT) {
            lv_obj_add_event_cb(b->w->obj, input_click_cb, LV_EVENT_CLICKED, b->w);
        }
        if (b->prop == BIND_CHECKED &&
            (b->w->type == W_SWITCH || b->w->type == W_CHECKBOX)) {
            lv_obj_add_event_cb(b->w->obj, writeback_cb, LV_EVENT_VALUE_CHANGED,
                                (void*)(intptr_t)(((intptr_t)b->tag_id << 8) | 1));
        } else if (b->prop == BIND_VALUE && b->w->type == W_ROLLER) {
            lv_obj_add_event_cb(b->w->obj, writeback_cb, LV_EVENT_VALUE_CHANGED,
                                (void*)(intptr_t)(((intptr_t)b->tag_id << 8) | 0));
        } else if (b->prop == BIND_VALUE && b->w->type == W_SLIDER) {
            lv_obj_add_event_cb(b->w->obj, writeback_cb, LV_EVENT_VALUE_CHANGED,
                                (void*)(intptr_t)(((intptr_t)b->tag_id << 8) | 0));
        } else if (b->prop == BIND_VALUE && b->w->type == W_KNOB) {
            lv_obj_add_event_cb(b->w->obj, writeback_cb, LV_EVENT_VALUE_CHANGED,
                                (void*)(intptr_t)(((intptr_t)b->tag_id << 8) | 0));
        }
    }

    /* 输入框：键盘只在**有写回变量**时才挂得上（点一下要写到哪里？）。
     * 没绑定的必须留一条日志 —— 否则现象就是"点了没反应"，只能靠猜。 */
    for (i = 0; i < (size_t)s_nwidgets; i++) {
        const char* wid;

        if (s_widgets[i].type != W_INPUT || s_widgets[i].bind_tag >= 0) continue;
        wid = i < (size_t)s_nflat
                ? hmi_json_str(hmi_json_get(s_wnodes[i], "id"), "?")
                : "?";
        HMI_LOG("input %s: 没有绑定变量，点击不会弹键盘（键盘需要把值写回某个变量）", wid);
    }

    /* 键盘控件：必须放在绑定写回之后——target 的 bind_tag 是那一步才设上的，
     * 早一步读会拿到 memset 的 0，键盘就会挂到 tag 0 上。 */
    if (link_keyboards(page_id, s_wnodes, s_widgets, s_nflat, err, errsz) != 0) {
        hmi_json_free(page);
        page_teardown();
        return -1;
    }

    /* 事件（37 §6.2）：click / press / release / change / long_press */
    for (i = 0; i < (size_t)s_nflat; i++) {
        const hmi_json_t* wj = s_wnodes[i];
        const hmi_json_t* events = hmi_json_get(wj, "events");
        const char* wid = hmi_json_str(hmi_json_get(wj, "id"), "?");
        int lp_hooked = 0;

        if (hmi_json_type(events) != HMI_JSON_OBJ) continue;
        for (size_t k = 0; k < hmi_json_count(events); k++) {
            const char* ev_name = hmi_json_key_at(events, k);
            const hmi_json_t* ev = hmi_json_at(events, k);
            int slot, is_lp = 0;

            lv_event_code_t code = LV_EVENT_CLICKED;

            if (!ev_name) continue;
            if (strcmp(ev_name, "click") == 0) code = LV_EVENT_CLICKED;
            else if (strcmp(ev_name, "press") == 0) code = LV_EVENT_PRESSED;
            else if (strcmp(ev_name, "release") == 0) code = LV_EVENT_RELEASED;
            else if (strcmp(ev_name, "change") == 0) code = LV_EVENT_VALUE_CHANGED;
            else if (strcmp(ev_name, "long_press") == 0) is_lp = 1;
            else {
                snprintf(err, errsz,
                         "控件 %s: 事件 %s 暂未支持（click/press/release/change/long_press）",
                         wid, ev_name);
                goto ev_fail;
            }
            slot = parse_actions(ev, wid, err, errsz);
            if (slot < 0) goto ev_fail;
            if (slot > 0 && s_widgets[i].obj) {
                s_actions[slot - 1].perm_op = s_widgets[i].poper;   /* 动作按控件权限判 */
                if (is_lp) {
                    /* 长按阈值是每个按钮自己的 long_press_ms，LVGL 只有全局的一份，
                     * 所以自己起一次性定时器：到点执行动作并标记"这次按下已算长按"，
                     * 松开时那次 click 会被 action_cb 吞掉。 */
                    s_widgets[i].lp_slot = slot - 1;
                    if (!lp_hooked) {
                        lv_obj_add_event_cb(s_widgets[i].obj, hold_begin_cb,
                                            LV_EVENT_PRESSED, &s_widgets[i]);
                        lv_obj_add_event_cb(s_widgets[i].obj, hold_end_cb,
                                            LV_EVENT_RELEASED, &s_widgets[i]);
                        lv_obj_add_event_cb(s_widgets[i].obj, hold_end_cb,
                                            LV_EVENT_PRESS_LOST, &s_widgets[i]);
                        lv_obj_add_event_cb(s_widgets[i].obj, hold_end_cb,
                                            LV_EVENT_DELETE, &s_widgets[i]);
                        lp_hooked = 1;
                    }
                } else {
                    lv_obj_add_event_cb(s_widgets[i].obj, action_cb, code,
                                        (void*)(intptr_t)(slot - 1));
                }
            }
        }
    }

    /* 带占位符的文本：页面级定时器统一重算 */
    if (s_nph > 0 && !s_ph_timer)
        s_ph_timer = lv_timer_create(ph_tick_cb, 500, NULL);

    /* 页面标题：右上角暗色小字，不占控件区（w1 在左上） */
    title_s = hmi_json_str(hmi_json_get(page, "title"), NULL);
    if (title_s && *title_s) {
        lv_obj_t* t = lv_label_create(s_screen);
        char tb[160];

        i18n_render(title_s, tb, sizeof(tb));   /* 标题也能写 @id */
        lv_label_set_text(t, tb);
        lv_obj_set_pos(t, HMI_LCD_WIDTH - 220, 10);
        lv_obj_set_size(t, 200, 24);
        lv_obj_set_style_text_font(t, ui_font(16), 0);
        lv_obj_set_style_text_color(t, ui_color_hex("#9AA7B5"), 0);
        lv_obj_set_style_text_align(t, LV_TEXT_ALIGN_RIGHT, 0);
        lv_obj_set_style_border_width(t, 0, 0);
        lv_obj_set_style_bg_opa(t, LV_OPA_TRANSP, 0);
        lv_obj_set_style_pad_all(t, 0, 0);
    }

    /* 页面脚本（37 §7.4）：on_open 建好之后跑（此时 cg 与绑定都已就绪），
     * on_timer 按 timer_ms 周期跑，on_close 在拆页前跑。 */
    {
        const char* on_open = hmi_json_str(hmi_json_get(page, "on_open"), "");
        const char* on_timer = hmi_json_str(hmi_json_get(page, "on_timer"), "");
        const char* on_close = hmi_json_str(hmi_json_get(page, "on_close"), "");
        long timer_ms = hmi_json_int(hmi_json_get(page, "timer_ms"), 0);

        free(s_on_timer);
        free(s_on_close);
        s_on_timer = (on_timer && *on_timer) ? strdup(on_timer) : NULL;
        s_on_close = (on_close && *on_close) ? strdup(on_close) : NULL;

        if (on_open && *on_open) {
            char serr[256];

            if (lua_rt_dostring(on_open, "=<page>.on_open", serr, sizeof(serr)) != 0)
                HMI_LOG("page: on_open 失败：%s", serr);
        }
        s_on_timer_fails = 0;
        if (s_on_timer && timer_ms > 0)
            s_script_timer = lv_timer_create(script_timer_cb, (uint32_t)timer_ms, NULL);
        else if (s_on_timer)
            HMI_LOG("page: on_timer 非空但 timer_ms=%ld，视为不启用", timer_ms);
    }

    hmi_json_free(page);
    layers_free();            /* 图层 JSON 只是建页期的视图，建完就释放 */

    s_refresh_timer = lv_timer_create(refresh_timer_cb, REFRESH_MS, NULL);
    /* 首帧立即应用一次绑定：定时器第一次触发要等 REFRESH_MS，之前画面是
     * 控件的初值（空文本、灯全亮），首屏不能是这个状态 */
    bind_table_refresh(s_bindings, s_nbindings);
    ui_anim_start_load();          /* trigger=load 的那批，首帧值就位后起跑 */
    lv_obj_invalidate(s_screen);
    HMI_LOG("page: %s 打开（控件 %d，绑定 %d，动作 %d）",
            page_id, s_nwidgets, s_nbindings, s_nactions);
    return 0;

ev_fail:
    hmi_json_free(page);
    page_teardown();
    return -1;
}

/* 切页：先摆一个空屏再拆旧页，避免删活动屏时 LVGL 把事件派给已释放对象；
 * 新页建失败就退回原页（页面 JSON 已经验证过一次，回退几乎不会失败）。 */
static void page_switch_apply(void)
{
    int op = s_pending_op;
    char target[64], fallback[64], err[PROJ_ERR_MAX];
    lv_obj_t* stub;

    s_pending_op = 0;
    if (!s_proj) return;

    snprintf(fallback, sizeof(fallback), "%s",
             (s_depth > 0) ? s_stack[s_depth - 1] : "");

    if (op == PAGE_OP_BACK || op == PAGE_OP_CLOSE) {
        if (s_depth <= 1) {
            HMI_LOG("page: 已在栈底，%s 无效",
                    op == PAGE_OP_BACK ? "back" : "close_page");
            return;
        }
        snprintf(target, sizeof(target), "%s", s_stack[s_depth - 2]);
    } else {
        snprintf(target, sizeof(target), "%s", s_pending_page);
        if (op == PAGE_OP_OPEN && s_depth >= PAGE_STACK_MAX) {
            HMI_LOG("page: 页面栈已满（%d），open_page %s 被拒绝",
                    PAGE_STACK_MAX, target);
            return;
        }
    }

    stub = lv_obj_create(NULL);
    if (stub) lv_screen_load(stub);

    page_teardown();
    if (build_page(s_proj, s_sim, target, NULL, 0, err, sizeof(err)) != 0) {
        HMI_LOG("page: 切到 %s 失败：%s（退回 %s）", target, err, fallback);
        if (fallback[0] && build_page(s_proj, s_sim, fallback, NULL, 0, err, sizeof(err)) != 0)
            HMI_LOG("page: 回退到 %s 也失败：%s", fallback, err);
        if (stub && lv_obj_is_valid(stub)) lv_obj_delete(stub);
        return;
    }
    if (stub && lv_obj_is_valid(stub)) lv_obj_delete(stub);

    if (op == PAGE_OP_OPEN) {
        snprintf(s_stack[s_depth], sizeof(s_stack[0]), "%s", target);
        s_depth++;
    } else if (op == PAGE_OP_GOTO) {
        snprintf(s_stack[s_depth - 1], sizeof(s_stack[0]), "%s", target);
    } else {
        s_depth--;              /* back / close_page：弹出栈顶 */
    }
    HMI_LOG("page: 栈深 %d", s_depth);
}

int page_mgr_open(const project_t* proj, sim_dev_t* sim, const char* page_id,
                  char* err, size_t errsz)
{
    s_proj = proj;
    s_sim = sim;
    s_depth = 0;
    s_pending_op = 0;
    if (build_page(proj, sim, page_id, NULL, 0, err, errsz) != 0) return -1;
    snprintf(s_stack[0], sizeof(s_stack[0]), "%s", page_id);
    s_depth = 1;
    return 0;
}

/* 重建当前页：登录/登出/空闲超时后，控件的可见与禁用要按新身份重算（ADR-0012） */
void page_mgr_request_reload(const char* toast)
{
    s_pending_reload = 1;
    snprintf(s_pending_toast, sizeof(s_pending_toast), "%s", toast ? toast : "");
}

int ui_page_patch_rect(const char* widget_id, int x, int y, int w, int h,
                       char* err, size_t errsz)
{
    if (!s_screen) {
        snprintf(err, errsz, "没有打开的页面");
        return -1;
    }
    if (!widget_id || !*widget_id) {
        snprintf(err, errsz, "缺控件 id");
        return -1;
    }

    for (int i = 0; i < s_nwidgets; i++) {
        if (s_wids[i][0] == '\0' || strcmp(s_wids[i], widget_id) != 0) continue;
        if (!s_widgets[i].obj) break;

        lv_obj_set_pos(s_widgets[i].obj, (int32_t)x, (int32_t)y);
        if (w > 0 && h > 0) lv_obj_set_size(s_widgets[i].obj, (int32_t)w, (int32_t)h);
        lv_obj_update_layout(s_widgets[i].obj);
        return 0;
    }
    snprintf(err, errsz, "控件 %s 不在当前页", widget_id);
    return -1;
}

/*
 * 立即把所有绑定按当前变量值刷一遍。
 *
 * 平时靠 lv_timer 的 refresh_timer_cb 每 REFRESH_MS 跑一次，但设计画布走的是
 * runtime_render_once（只 lv_refr_now，不跑 lv_timer_handler）—— 少这一步，
 * 键盘提交写进变量后框里还是旧文字，其它绑定控件也不动。
 */
void ui_page_refresh_bindings(void)
{
    bind_table_refresh(s_bindings, s_nbindings);
}

int page_mgr_reload(void)
{
    char err[192];
    char cur[64];

    if (s_depth <= 0) return -1;
    snprintf(cur, sizeof(cur), "%s", s_stack[s_depth - 1]);
    page_teardown();
    if (build_page(s_proj, s_sim, cur, NULL, 0, err, sizeof(err)) != 0) {
        HMI_LOG("page: 重建 %s 失败：%s", cur, err);
        return -1;
    }
    return 0;
}

/*
 * 用调用方推来的页面 JSON 重建当前页。
 *
 * 为什么必须能推：设计画布拖动只改了编辑器模型，宿主里那份工程还是导出时的快照。
 * 若照旧从 vfs 读 pages/<id>.json 重建，刚拖到新位置的控件会被"重载"拉回旧坐标
 * （实测现象：松手后画面回原位、选中框不动）。所以重载必须吃最新的页面 JSON。
 */
int page_mgr_reload_json(const char* json, size_t len, char* err, size_t errsz)
{
    char cur[64];
    char inner[192];

    if (s_depth <= 0) {
        snprintf(err, errsz, "没有打开的页面");
        return -1;
    }
    if (!json || len == 0) {
        snprintf(err, errsz, "页面 JSON 为空");
        return -1;
    }
    snprintf(cur, sizeof(cur), "%s", s_stack[s_depth - 1]);
    page_teardown();
    if (build_page(s_proj, s_sim, cur, json, len, inner, sizeof(inner)) != 0) {
        snprintf(err, errsz, "%s", inner);
        HMI_LOG("page: 用推送的 JSON 重建 %s 失败：%s", cur, inner);
        return -1;
    }
    return 0;
}

void page_mgr_request(int op, const char* page_id)
{
    if (!s_proj) return;
    if (op == PAGE_OP_GOTO || op == PAGE_OP_OPEN) {
        if (!page_id || !*page_id) {
            HMI_LOG("page: 切页动作缺 page 参数");
            return;
        }
        snprintf(s_pending_page, sizeof(s_pending_page), "%s", page_id);
    }
    s_pending_op = op;
}

int page_mgr_script_open(const char* page_id)
{
    /* 脚本切页与切页动作同权（ADR-0026）：目标页的 perm 组掩码由建页期判，
     * 命名 view 位在这里先挡一道，免得脚本拿到"建页失败转错误页"的结果 */
    if (!auth_perm(PERM_VIEW)) {
        HMI_LOG("auth: 拒绝脚本打开页面 %s（缺少 view 权限）", page_id ? page_id : "?");
        audit_denied("cg.openScreen", "view");
        return -1;
    }
    page_mgr_request(PAGE_OP_OPEN, page_id);
    return 0;
}

int page_mgr_script_close(void)
{
    page_mgr_request(PAGE_OP_CLOSE, NULL);
    return 0;
}

int page_mgr_script_is_open(const char* page_id)
{
    if (!page_id || s_depth <= 0) return 0;
    return strcmp(s_stack[s_depth - 1], page_id) == 0;
}

void page_mgr_close(void)
{
    /* 先切回原活动屏再删：删掉活动屏会让 LVGL 持悬垂指针 */
    if (s_screen && s_default_screen && lv_screen_active() == s_screen)
        lv_screen_load(s_default_screen);
    page_teardown();
    s_depth = 0;
    s_pending_op = 0;
    s_pending_reload = 0;
    s_pending_toast[0] = '\0';
    s_proj = NULL;
    s_sim = NULL;
    s_default_screen = NULL;
}

int page_mgr_is_open(void)
{
    return s_screen != NULL;
}
