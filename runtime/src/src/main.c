/*
 * chenge-runtime — 组态工程渲染运行时（进程版入口）
 *
 * 启动链（39 §2）：chenge-launcher 判定后 fork/exec 本进程；要回设置就
 * exit(HMI_EXIT_TO_CONFIG)，由 launcher 去拉起 chenge-config，不自己 exec。
 *
 * 启动序列与单步主循环在 runtime_boot.c（嵌入版 embed.c 共用同一份）；
 * 本文件只保留"进程版独有的东西"：内存水位日志、失败提示页、主循环节拍、
 * 宿主自检入口。改了启动顺序要去 runtime_boot.c 改，不要在这里复制一份。
 *
 * 两条失败路径都不黑屏、不崩溃循环：无工程 → 提示页；加载失败 → 错误页
 * （显示首条错误：文件 + 路径 + 原因）；两者都是触摸或 10 秒后 exit(41)。
 *
 * 宿主验证（不碰 fb0/evdev，走 SDL2 窗口）：
 *   HMI_PROJECT_DIR=<工程目录> HMI_FONT_DIR=<TTF 目录> \
 *   HMI_SHOT=<a.bmp> ./chenge-runtime
 *
 * 查能力（不初始化显示与工程，输出一行 JSON）：
 *   ./chenge-runtime --caps
 */
#include "lvgl.h"
#include "hmi_board.h"     /* HMI_LCD_WIDTH/HEIGHT */
#include "hmi_display.h"   /* hmi_platform_poll */
#include "hmi_exit.h"
#include "hmi_log.h"

#include "core/caps.h"
#include "script/lua_rt.h"
#include "project/loader.h"
#include "runtime_boot.h"
#include "ui/ui_font.h"
#include "ui/ui_page.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define MSG_PAGE_TIMEOUT_MS 10000

/* ── 内存水位 ──
 * 板端拿内存数字只能读 /proc，日志里留一份便于回归对比（12MB 是硬指标）。
 * 每 60 秒只在与上次相差超过 64KB 时打一行：正常运行期 RSS 稳定，不该刷屏。 */
static int mem_read(long* hwm, long* rss)
{
    FILE* f = fopen("/proc/self/status", "r");
    char line[128];
    long h = -1, r = -1;

    if (!f) return -1;
    while (fgets(line, sizeof(line), f)) {
        if (strncmp(line, "VmHWM:", 6) == 0) h = strtol(line + 6, NULL, 10);
        else if (strncmp(line, "VmRSS:", 6) == 0) r = strtol(line + 6, NULL, 10);
    }
    fclose(f);
    if (h < 0 || r < 0) return -1;
    *hwm = h;
    *rss = r;
    return 0;
}

static long s_rss_mark;

static void mem_log(const char* tag)
{
    long hwm, rss;

    if (mem_read(&hwm, &rss) == 0)
        HMI_LOG("mem[%s]: VmHWM=%ldkB VmRSS=%ldkB", tag, hwm, rss);
}

static void mem_timer_cb(lv_timer_t* t)
{
    long hwm, rss;

    LV_UNUSED(t);
    if (mem_read(&hwm, &rss) != 0) return;
    if (s_rss_mark > 0 && labs(rss - s_rss_mark) <= 64) return;
    s_rss_mark = rss;
    HMI_LOG("mem[tick]: VmHWM=%ldkB VmRSS=%ldkB", hwm, rss);
}

/* ── 提示页 / 错误页 ── */

static int s_msg_exit;      /* 页面被点过：置 1 后等待循环返回 */

static void msg_clicked_cb(lv_event_t* e)
{
    LV_UNUSED(e);
    s_msg_exit = 1;
}

static void msg_line(lv_obj_t* parent, int y, const char* text, int size,
                     uint32_t rgb)
{
    lv_obj_t* l = lv_label_create(parent);

    lv_label_set_text(l, text);
    lv_obj_set_pos(l, 40, y);
    lv_obj_set_size(l, HMI_LCD_WIDTH - 80, size + 16);
    lv_obj_set_style_text_font(l, ui_font(size), 0);
    lv_obj_set_style_text_color(l, lv_color_hex(rgb), 0);
    lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP);
    lv_obj_set_style_border_width(l, 0, 0);
    lv_obj_set_style_bg_opa(l, LV_OPA_TRANSP, 0);
    lv_obj_set_style_pad_all(l, 0, 0);
}

static void show_message_page(const char* title, const char* line1,
                              const char* line2)
{
    lv_obj_t* page = lv_obj_create(NULL);
    int h = HMI_LCD_HEIGHT;

    s_msg_exit = 0;
    lv_obj_set_size(page, HMI_LCD_WIDTH, h);
    lv_obj_set_pos(page, 0, 0);
    lv_obj_set_style_pad_all(page, 0, 0);
    lv_obj_set_style_border_width(page, 0, 0);
    lv_obj_set_style_radius(page, 0, 0);
    lv_obj_set_style_bg_color(page, lv_color_hex(0x1A1414), 0);
    lv_obj_set_style_bg_opa(page, LV_OPA_COVER, 0);
    lv_obj_clear_flag(page, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_event_cb(page, msg_clicked_cb, LV_EVENT_CLICKED, NULL);
    lv_screen_load(page);

    msg_line(page, 150, title, 28, 0xF28B82);
    if (line1 && *line1) msg_line(page, 210, line1, 18, 0xE8C8C8);
    if (line2 && *line2) msg_line(page, 250, line2, 18, 0xE8C8C8);
    msg_line(page, h - 90, "触摸屏幕或 10 秒后进入配置界面", 16, 0x94A3B8);
}

/* 等一次触摸或超时；期间必须继续泵 LVGL 与平台事件，否则画面不刷新 */
static void wait_touch_or_timeout(int ms)
{
    long long t0 = rt_now_ms();

    while (!s_msg_exit && rt_now_ms() - t0 < ms) {
        lv_timer_handler();
        hmi_platform_poll();
        usleep(5000);
    }
}

int main(int argc, char** argv)
{
    project_t proj;
    char err[PROJ_ERR_MAX];
    int rc;

    /* --caps：只打印能力表，不初始化 LVGL/显示/工程（板端排查与编辑器核对用） */
    if (argc > 1 && strcmp(argv[1], "--caps") == 0) {
        caps_print(stdout);
        return 0;
    }

    setvbuf(stderr, NULL, _IOLBF, 0);
    HMI_LOG("chenge-runtime %s start", APP_VERSION);

    rc = runtime_boot(&proj, NULL, err, sizeof(err));
    if (rc != RT_BOOT_OK) {
        if (rc == RT_BOOT_NO_PROJECT) {
            HMI_LOG("runtime: %s", err);
            show_message_page("未安装组态工程",
                              "把工程目录（含 manifest.json）放到 U 盘，",
                              "在配置界面「维护 → 更新组态」导入。");
        } else {
            HMI_LOG("runtime: 工程加载失败：%s", err);
            show_message_page("工程加载失败", err, "");
        }
        wait_touch_or_timeout(MSG_PAGE_TIMEOUT_MS);
        return HMI_EXIT_TO_CONFIG;
    }

    mem_log("ready");                       /* 建页完成即 v1 的内存峰值 */
    lv_timer_create(mem_timer_cb, 60000, NULL);
    HMI_LOG("runtime: 进入主循环（轮询 %dms）", project_poll_ms(&proj));

#ifdef HMI_HOST_BUILD
    /* 宿主自检：HMI_LUA_RUN="<lua 代码>" 跑一段脚本并把结果打进日志。
     * 只编进宿主版——板端没有 shell，不该留这种入口。 */
    {
        const char* snip = getenv("HMI_LUA_RUN");

        if (snip && *snip) {
            char val[128], lerr[256];

            if (lua_rt_eval_str(snip, val, sizeof(val), lerr, sizeof(lerr)) == 0)
                HMI_LOG("lua[selftest]: %s  =>  %s", snip, val);
            else
                HMI_LOG("lua[selftest]: %s  =>  失败：%s", snip, lerr);
        }
    }
#endif

    for (;;) {
        runtime_tick();
        usleep(5000);
    }
    return 0;
}
