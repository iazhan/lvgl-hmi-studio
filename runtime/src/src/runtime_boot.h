/*
 * runtime_boot —— 启动序列（定位/装载/子系统/建起始页）与单步主循环。
 *
 * 两个宿主共用这一段：进程版 main.c（独立运行、SDL 或 /dev/fb0）与嵌入版
 * embed.c（被编辑器加载成 .so，帧直接交出去）。分开写两份的代价是"导出能用、
 * 预览不能用"这类漂移，所以序列只在这里有一份。
 */
#ifndef CHENGE_RUNTIME_BOOT_H
#define CHENGE_RUNTIME_BOOT_H

#include <stddef.h>
#include <stdint.h>

#include "project/loader.h"

/* 启动结果。除 OK 外都由调用方决定怎么显示（进程版画提示页，嵌入版把文案回给编辑器）。 */
typedef enum {
    RT_BOOT_OK = 0,
    RT_BOOT_NO_PROJECT = 1,   /* 没有已安装的工程（HMI_PROJECT_DIR 未设且 active 不存在） */
    RT_BOOT_BAD_ROOT = 2,     /* 工程根定位失败（路径不可读等） */
    RT_BOOT_LOAD_FAIL = 3,    /* manifest / tags / devices 等解析失败 */
    RT_BOOT_TAGDB_FAIL = 4,   /* 变量表内存不足 */
    RT_BOOT_SUBSYS_FAIL = 5,  /* strings / alarms / recipes / users 加载失败 */
    RT_BOOT_PAGE_FAIL = 6,    /* 起始页建页失败（导出不合法多半落在这里） */
} rt_boot_rc;

/*
 * 完整启动：lv_init（仅首次）→ 显示/输入 → 定位工程根（forced_root 非空则用它）
 * → 装载 → Tag DB / sim / 趋势 / 多语言 / 报警 / 配方 / 用户 → 建起始页
 * → sim 定时器 + Modbus 线程。
 *
 * 失败时把原因写进 err（errlen 含结尾 NUL），proj 是否需要 project_free 由返回值决定：
 * 只有 RT_BOOT_OK 时 proj 归调用方所有。
 */
int runtime_boot(project_t* proj, const char* forced_root, char* err, size_t errlen);

/*
 * 主循环的一拍：lv_timer_handler + 平台 poll + 100ms/1s 节流子系统。
 * 不睡眠 —— 进程版自己 usleep(5000)，嵌入版由调用方按刷新节拍调。
 */
void runtime_tick(void);

/*
 * 只重绘一帧：不推进 LVGL timer，也不跑 100ms/1s 节流子系统。
 *
 * 设计画布要的是"静止的真图"——一旦推进时间，报警条会滚、曲线会采样、
 * 管道会流动，同一份工程每次画出不同像素，既不能逐像素对拍，也会让用户
 * 以为设计态"在跑"。属性补丁改完几何后由宿主调这个把新画面刷出来。
 */
void runtime_render_once(void);

/* 尽力释放（页面/存盘/趋势/变量表）。子系统里没有 teardown 的部分留着不动。 */
void runtime_shutdown(void);

/* LVGL tick 源（CLOCK_MONOTONIC，毫秒）。宿主也可以自己 lv_tick_set_cb 覆盖。 */
uint32_t rt_tick_get(void);

/* 单调毫秒（CLOCK_MONOTONIC）。节流判断都用它，别混用 time()。 */
long long rt_now_ms(void);

#endif /* CHENGE_RUNTIME_BOOT_H */
