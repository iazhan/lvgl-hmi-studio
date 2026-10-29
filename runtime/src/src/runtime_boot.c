/*
 * 启动序列与单步主循环 —— 进程版（main.c）与嵌入版（embed.c）共用。
 *
 * 这里刻意**不做**任何"显示在哪"的决定：显示后端由平台层二选一/三选一
 * （host=SDL、board=/dev/fb0、embed=调用方拿帧），本文件只假设
 * hmi_lcd_init()/hmi_input_init()/hmi_platform_poll() 已由链接期选定。
 */
#include "lvgl.h"
#include "hmi_board.h"
#include "hmi_display.h"
#include "hmi_log.h"

#include "comm/modbus.h"
#include "comm/sim.h"
#include "comm/sim_cfg.h"
#include "core/alarm.h"
#include "core/audit.h"
#include "core/auth.h"
#include "core/i18n.h"
#include "core/recipe.h"
#include "core/sysvar.h"
#include "core/tagdb.h"
#include "core/tagwrite.h"
#include "data/savecfg.h"
#include "data/trend_logger.h"
#include "data/user_store.h"
#include "project/loader.h"
#include "runtime_boot.h"
#include "script/cg_api.h"
#include "script/lua_rt.h"
#include "script/strategies.h"
#include "ui/ui_font.h"
#include "ui/ui_page.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

/* 只有平台层是 hmi_ui_host.c（SDL）时才存在：让 HMI_HOST_CLICK 从"页面建好"起算 */
extern void hmi_host_click_arm(void) __attribute__((weak));

static sim_dev_t s_sim_dev;
static long long s_last_tick_ms;
static long long s_last_alarm_ms;      /* 报警扫描的上一拍（主循环里按 100ms 节流） */
static long long s_last_sysvar_ms;     /* 系统变量的上一拍（1s 节流） */
static int       s_trace = -1;         /* HMI_RT_TRACE 只读一次 */
static int       s_lv_inited;
static int       s_booted;

uint32_t rt_tick_get(void)
{
    static uint64_t start_ms;
    struct timespec ts;
    uint64_t now;

    clock_gettime(CLOCK_MONOTONIC, &ts);
    now = (uint64_t)ts.tv_sec * 1000 + (uint64_t)ts.tv_nsec / 1000000;
    if (start_ms == 0) start_ms = now;
    return (uint32_t)(now - start_ms);
}

long long rt_now_ms(void)
{
    struct timespec ts;

    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (long long)ts.tv_sec * 1000 + (long long)ts.tv_nsec / 1000000;
}

/* ── sim 定时器：按设备 poll_ms 生成值并同步进 Tag DB ── */

static void sim_timer_cb(lv_timer_t* t)
{
    const project_t* proj = (const project_t*)lv_timer_get_user_data(t);
    long long now = rt_now_ms();

    /* 生成器的周期以毫秒计时，定时器周期（设备 poll_ms）只决定读回频率 */
    sim_tick(&s_sim_dev, (uint32_t)(now - s_last_tick_ms));
    sim_cfg_tick(&s_sim_dev, (uint32_t)now);
    s_last_tick_ms = now;
    sim_sync_tags(&s_sim_dev, proj->tags, proj->tags_count);

    if (s_trace < 0) {
        const char* e = getenv("HMI_RT_TRACE");

        s_trace = (e && e[0] == '1') ? 1 : 0;
    }
    if (s_trace) {
        char line[256];
        int n = 0;

        for (int i = 0; i < proj->tags_count && n < (int)sizeof(line) - 24; i++) {
            const tag_t* tg = tagdb_at(i);

            /* 与界面一致：质量不是 GOOD 就打 ---，别让人以为读到了值 */
            if (tagdb_quality(i) != TAG_Q_GOOD) {
                n += snprintf(line + n, sizeof(line) - (size_t)n, "%s=--- ",
                              proj->tags[i].name);
                continue;
            }
            if (tg && tg->type == TAG_T_BOOL)
                n += snprintf(line + n, sizeof(line) - (size_t)n, "%s=%d ",
                              proj->tags[i].name, tagdb_bool(i));
            else if (tg && tg->type == TAG_T_I64)
                n += snprintf(line + n, sizeof(line) - (size_t)n, "%s=%lld ",
                              proj->tags[i].name, (long long)tagdb_i64(i));
            else if (tg && tg->type == TAG_T_STRING) {
                char sb[64];
                int sn = tagdb_str_copy(i, sb, (int)sizeof(sb) - 1, NULL);

                if (sn < 0) sn = 0;
                sb[sn] = '\0';
                n += snprintf(line + n, sizeof(line) - (size_t)n, "%s=\"%s\" ",
                              proj->tags[i].name, sb);
            } else
                n += snprintf(line + n, sizeof(line) - (size_t)n, "%s=%.1f ",
                              proj->tags[i].name, tagdb_num(i));
        }
        HMI_LOG("tick: %s", line);
    }
}

/*
 * 数据目录：HMI_DATA_DIR > /data/chenge（可写）> <cwd>/.hmi-data。
 * 嵌入版由编辑器给临时目录，绝不能写进工程目录 —— 导出是"可重复的产物"。
 */
static const char* data_dir(char* buf, size_t n)
{
    const char* d = getenv("HMI_DATA_DIR");

    if (d && *d) return d;
    d = "/data/chenge";
    if (access(d, W_OK) == 0) return d;

    {
        char cwd[128] = ".";

        if (!getcwd(cwd, sizeof(cwd))) cwd[0] = '.';
        snprintf(buf, n, "%s/.hmi-data", cwd);
    }
    return buf;
}

static int fail(project_t* proj, char* err, size_t errlen, const char* what)
{
    HMI_LOG("runtime: %s：%s", what, err);
    project_free(proj);
    return RT_BOOT_SUBSYS_FAIL;
}

int runtime_boot(project_t* proj, const char* forced_root, char* err, size_t errlen)
{
    char root[256];
    int r;

    if (!proj || !err || errlen == 0) return RT_BOOT_BAD_ROOT;
    err[0] = '\0';

    if (!s_lv_inited) {
        lv_init();
        lv_tick_set_cb(rt_tick_get);
        s_lv_inited = 1;
    }

    if (hmi_lcd_init() != 0) {
        snprintf(err, errlen, "显示初始化失败");
        return RT_BOOT_BAD_ROOT;
    }
    if (hmi_input_init() != 0)
        HMI_LOG("输入初始化失败（无触摸继续运行）");

    /* 1) 工程根：嵌入版由调用方直接给目录（不给就按进程版的规则找） */
    if (forced_root && *forced_root) {
        snprintf(root, sizeof(root), "%s", forced_root);
    } else {
        r = project_resolve_root(root, sizeof(root), err, errlen);
        if (r == PROJECT_RESOLVE_NONE) return RT_BOOT_NO_PROJECT;
        if (r == PROJECT_RESOLVE_ERR) return RT_BOOT_BAD_ROOT;
    }

    /* 2) 装载 manifest / ports / devices / tags */
    if (project_load(root, proj, err, errlen) != 0) return RT_BOOT_LOAD_FAIL;

    HMI_LOG("runtime: 工程 %s v%s（格式 %s，最低运行时 %s，屏幕 %dx%d，%s）",
            proj->manifest.name, proj->manifest.version,
            proj->manifest.format_version, proj->manifest.min_runtime,
            proj->manifest.screen_w, proj->manifest.screen_h,
            proj->manifest.hw_profile);
    HMI_LOG("runtime: 起始页 %s，变量 %d，设备 %d，串口 %d，字号 %d 档",
            proj->manifest.start_page, proj->tags_count, proj->devices_count,
            proj->ports_count, proj->manifest.font_size_count);

    /* 3) Tag DB + sim 初值 */
    sysvar_init();
    if (tagdb_init(proj->tags_count) != 0) {
        snprintf(err, errlen, "变量表分配失败（内存不足，%d 个变量）", proj->tags_count);
        project_free(proj);
        return RT_BOOT_TAGDB_FAIL;
    }
    project_apply_tags(proj);
    sim_init(&s_sim_dev);
    /* 模拟设备（ADR-0015）：sim.json 的生成器与常量、sim.lua 的逻辑模拟。
     * 出问题只跳过模拟，不拦启动。 */
    {
        char serr[192] = { 0 };

        (void)sim_cfg_load(proj, &s_sim_dev, serr, sizeof(serr));
        if (serr[0]) HMI_LOG("runtime: 模拟设备有问题：%s", serr);
    }
    sim_sync_tags(&s_sim_dev, proj->tags, proj->tags_count);

    /* 趋势/存盘/多语言/报警/配方/用户：顺序有依赖，别按字母重排 */
    {
        char tbuf[192];
        const char* tdir = data_dir(tbuf, sizeof(tbuf));

        ui_set_trend_dir(tdir);
        trend_logger_start(proj, tdir);
        (void)savecfg_start(proj, tdir);
        /* 多语言要在报警与建页之前 —— 报警的 text_id 加载期就要查词表 */
        if (i18n_load(proj, tdir, err, errlen) < 0) return fail(proj, err, errlen, "strings.json 加载失败");
        if (alarm_load(proj, tdir, err, errlen) < 0) return fail(proj, err, errlen, "报警定义加载失败");
        if (recipe_load(proj, tdir, err, errlen) < 0) return fail(proj, err, errlen, "配方加载失败");

        audit_init(tdir);
        if (user_store_load(proj, tdir, err, errlen) < 0) return fail(proj, err, errlen, "用户表加载失败");
        user_store_password_policy(proj->manifest.password_min_len);
        {
            auth_cfg_t acfg;

            memset(&acfg, 0, sizeof(acfg));
            acfg.idle_min = proj->manifest.idle_min;
            acfg.session_timeout_min = proj->manifest.session_timeout_min;
            acfg.max_login_attempts = proj->manifest.max_login_attempts;
            acfg.lockout_min = proj->manifest.lockout_min;
            acfg.default_user = proj->manifest.default_user;
            (void)auth_init(&acfg);
        }
    }

    /* 脚本层先就绪：页面的 on_open 里就要用 cg.* */
    if (lua_rt_init() == 0) cg_api_attach(proj);
    /* 写变量要下发到设备（否则配方下载会被轮询覆盖回旧值） */
    tagwrite_init(proj, &s_sim_dev);
    {
        int n = recipe_apply_on_start();

        if (n > 0) HMI_LOG("runtime: 开机下发 %d 组配方", n);
    }

    /* 4) 建起始页 */
    if (page_mgr_open(proj, &s_sim_dev, proj->manifest.start_page, err, errlen) != 0) {
        project_free(proj);
        return RT_BOOT_PAGE_FAIL;
    }

    s_last_tick_ms = rt_now_ms();
    s_last_alarm_ms = s_last_tick_ms;
    s_last_sysvar_ms = s_last_tick_ms;
    lv_timer_create(sim_timer_cb, (uint32_t)project_poll_ms(proj), proj);
    strategies_start(proj);
    {
        int nthread = modbus_start(proj);

        if (nthread < 0)
            HMI_LOG("runtime: Modbus 线程一个都没起来（串口打不开？）");
    }

    /* 宿主 SDL 版才有这个钩子（板端/嵌入版没有该符号，弱引用为 NULL）：
     * 页面建好这一刻，HMI_HOST_CLICK 的 delay 才开始计时 —— 建页耗时随负载浮动，
     * 从进程启动起算会让预置点击落在空屏上。 */
    if (hmi_host_click_arm) hmi_host_click_arm();

    s_booted = 1;
    return RT_BOOT_OK;
}

void runtime_render_once(void)
{
    if (!s_booted) return;

    /* 设计画布要"静止的真图"：先清掉所有 LVGL 动画再重绘。
     *
     * spinner 建的时候 lv_spinner_set_anim_params 会起两条无限循环动画，
     * early_apply 让它创建瞬间落到起始角 —— 而每次整页重建（改属性/增删控件）
     * 都会重新建对象、重新起跑，于是每刷新一次相位就变一次，看起来"加载动画
     * 每次都在动"。lv_anim_delete(NULL, NULL) 匹配全部动画，删掉即可；
     * 仿真模式走 runtime_tick，不受这里影响。 */
    lv_anim_delete(NULL, NULL);

    /* 绑定按变量值刷一遍再重绘：设计画布不跑 lv_timer_handler，刷新定时器永远不会触发，
     * 少了这句"键盘提交后输入框里还是旧文字"。 */
    ui_page_refresh_bindings();

    lv_refr_now(NULL);
    hmi_platform_poll();
}

void runtime_tick(void)
{
    long long now;

    if (!s_booted) return;
    now = rt_now_ms();

    lv_timer_handler();
    hmi_platform_poll();

    /* 系统变量：每秒刷一次（时间/日期/报警计数），值没变不会动 version */
    if (now - s_last_sysvar_ms >= 1000) {
        s_last_sysvar_ms = now;
        sysvar_tick();
    }
    /* 报警扫描：主循环 100ms 一拍（ADR-0008 决定 2，不开独立线程） */
    if (now - s_last_alarm_ms >= 100) {
        s_last_alarm_ms = now;
        alarm_tick();
        savecfg_tick();
        auth_tick();                    /* 空闲超时检查（ADR-0012） */
        /* 空闲超时后 auth 会清会话：检测到身份变了就重建当前页（ADR-0012）。
         * 换人不换"在线状态"（op → guest）也要重建，否则控件的显隐还按旧组算。 */
        {
            static int  was_in = -1;
            static char was_user[32];
            int now_in = auth_logged_in();
            const char* now_name = now_in && auth_user_name() ? auth_user_name() : "";

            if (was_in >= 0 && (was_in != now_in || strcmp(was_user, now_name) != 0)) {
                sysvar_tick();              /* 重建前先把 sys.user 刷成新值 */
                (void)page_mgr_reload();
                HMI_LOG("auth: 身份变化（%s），重建当前页",
                        now_in ? now_name : "未登录");
            }
            was_in = now_in;
            snprintf(was_user, sizeof(was_user), "%s", now_name);
        }
    }
}

void runtime_shutdown(void)
{
    if (!s_booted) return;

    page_mgr_close();
    savecfg_stop();
    trend_logger_stop();
    tagdb_free();
    s_booted = 0;
}
