/*
 * caps.c — 能力表实现与 --caps 输出
 *
 * 纯查表，不读文件、不初始化任何子系统：--caps 要在显示与工程加载之前就能跑
 * （板端用它排查"这块固件到底有没有 mqtt"，宿主用它给编辑器核对能力）。
 */
#include "caps.h"
#include "hmi_limits.h"     /* HMI_WIDGETS_PER_PAGE：与 widgets.h 共用一份 */
#include "hmi_board.h"   /* HMI_BOARD_ID */

#include <stdio.h>
#include <string.h>

/* 版本由构建系统烘进来（common.mk 的 -DAPP_VERSION）；单独编译本文件时给个占位，
 * 免得为了打一张能力表还要带整套构建参数。 */
#ifndef APP_VERSION
#define APP_VERSION "0.0.0"
#endif

/* 上限：与 loader.h / widgets.h 的宏保持一致（tools/check_caps.py 逐项核对）。
 * 这里不再 include 那两个头，是为了让 --caps 不依赖工程模型与 LVGL。 */
#define CAPS_LIMIT_TAGS     2000
#define CAPS_LIMIT_DEVICES  64
#define CAPS_LIMIT_PORTS    8
#define CAPS_LIMIT_FONTS    8
#define CAPS_LIMIT_WIDGETS  HMI_WIDGETS_PER_PAGE

#define CAPS_MOD_ROW(id, on, desc) { id, (on) ? 1 : 0, desc },
static const struct {
    const char* id;
    int         on;
    const char* desc;
} s_modules[] = {
    HMI_MODULE_LIST(CAPS_MOD_ROW)
};
#undef CAPS_MOD_ROW

#define CAPS_FEATURE_ROW(feat, mod) { feat, mod },
static const struct {
    const char* id;
    const char* module;
} s_features[] = {
    HMI_FEATURE_LIST(CAPS_FEATURE_ROW)
};
#undef CAPS_FEATURE_ROW

#define MODULE_COUNT  ((int)(sizeof(s_modules) / sizeof(s_modules[0])))
#define FEATURE_COUNT ((int)(sizeof(s_features) / sizeof(s_features[0])))

int caps_module(const char* id)
{
    int i;

    if (!id || !*id) return -1;
    for (i = 0; i < MODULE_COUNT; i++) {
        if (strcmp(s_modules[i].id, id) == 0) return s_modules[i].on;
    }
    return -1;
}

int caps_feature_index(const char* id)
{
    int i;

    if (!id || !*id) return -1;
    for (i = 0; i < FEATURE_COUNT; i++) {
        if (strcmp(s_features[i].id, id) == 0) return i;
    }
    return -1;
}

const char* caps_feature_module(const char* id)
{
    int i;

    if (!id || !*id) return NULL;
    for (i = 0; i < FEATURE_COUNT; i++) {
        if (strcmp(s_features[i].id, id) == 0) return s_features[i].module;
    }
    return NULL;
}

int caps_feature(const char* id)
{
    const char* mod = caps_feature_module(id);

    if (!mod) return -1;
    /* 能力依赖的模块必须在表里；模块缺失是表写错了，按不可用处理而不是崩溃 */
    return caps_module(mod) == 1 ? 1 : 0;
}

void caps_print(FILE* out)
{
    int i, first;

    if (!out) return;

    fprintf(out, "{\"runtime\":\"%s\",\"hw_profile\":\"%s\",\"modules\":{",
            APP_VERSION, HMI_BOARD_ID);
    first = 1;
    for (i = 0; i < MODULE_COUNT; i++) {
        fprintf(out, "%s\"%s\":%s", first ? "" : ",",
                s_modules[i].id, s_modules[i].on ? "true" : "false");
        first = 0;
    }

    fprintf(out, "},\"features\":[");
    first = 1;
    for (i = 0; i < FEATURE_COUNT; i++) {
        if (caps_module(s_features[i].module) != 1) continue;
        fprintf(out, "%s\"%s\"", first ? "" : ",", s_features[i].id);
        first = 0;
    }

    fprintf(out, "],\"limits\":{\"tags\":%d,\"devices\":%d,\"ports\":%d,"
                 "\"fonts\":%d,\"widgets_per_page\":%d}}\n",
            CAPS_LIMIT_TAGS, CAPS_LIMIT_DEVICES, CAPS_LIMIT_PORTS,
            CAPS_LIMIT_FONTS, CAPS_LIMIT_WIDGETS);
}
