/*
 * strategies.c — 定时策略（37 §7.4）
 */
#include "strategies.h"

#include "expr.h"
#include "hmi_json.h"
#include "hmi_log.h"
#include "lua_rt.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "lvgl.h"

#define STRAT_MAX 8

typedef struct {
    char        name[32];
    int         ref;          /* Lua 函数引用 */
    int         has_cond;
    expr_prog_t cond;
    lv_timer_t* timer;
    int         fails;
} strategy_t;

static strategy_t s_items[STRAT_MAX];
static int s_count;

static void strat_cb(lv_timer_t* t)
{
    strategy_t* st = (strategy_t*)lv_timer_get_user_data(t);
    char err[256];

    if (!st) return;

    if (st->has_cond) {
        ex_val_t v;

        if (expr_eval(&st->cond, &v) != 0) return;       /* 条件求值失败：本周期不跑 */
        if (v.type == EX_V_STR ? !(v.s && v.s[0]) : (v.n == 0)) return;
    }

    if (lua_rt_call_ref(st->ref, err, sizeof(err)) == 0) {
        st->fails = 0;
        return;
    }
    if (++st->fails >= 3) {
        HMI_LOG("strategy %s: 连续失败 3 次，停用（最后错误：%s）", st->name, err);
        lv_timer_delete(t);
        st->timer = NULL;
    } else {
        HMI_LOG("strategy %s: 失败（%d/3）：%s", st->name, st->fails, err);
    }
}

int strategies_start(const project_t* p)
{
    char path[320], err[256];
    char* text;
    size_t len = 0;
    hmi_json_t* arr;
    size_t n;

    s_count = 0;
    snprintf(path, sizeof(path), "%s/strategies.json", p->root);
    text = loader_read_text(path, 256 * 1024, &len);
    if (!text) return 0;                 /* 没有策略文件是正常情况 */

    arr = hmi_json_parse(text, len, err, sizeof(err));
    if (!arr) {
        HMI_LOG("strategies.json 解析失败：%s", err);
        free(text);
        return -1;
    }
    if (hmi_json_type(arr) != HMI_JSON_ARR) {
        HMI_LOG("strategies.json 根节点不是数组");
        hmi_json_free(arr);
        free(text);
        return -1;
    }

    n = hmi_json_count(arr);
    for (size_t i = 0; i < n && s_count < STRAT_MAX; i++) {
        const hmi_json_t* e = hmi_json_at(arr, i);
        const char* name = hmi_json_str(hmi_json_get(e, "name"), "?");
        const char* trigger = hmi_json_str(hmi_json_get(e, "trigger"), "timer");
        const char* src = hmi_json_str(hmi_json_get(e, "source"), "");
        const char* cond = hmi_json_str(hmi_json_get(e, "condition"), "");
        long interval = hmi_json_int(hmi_json_get(e, "interval_ms"), 0);
        int enabled = hmi_json_bool(hmi_json_get(e, "enabled"), 0);
        strategy_t* st;

        if (!enabled) continue;
        if (strcmp(trigger, "timer") != 0) {
            HMI_LOG("strategy %s: trigger=%s 暂未实现（v1 只有 timer）", name, trigger);
            continue;
        }
        if (!src || !*src) {
            HMI_LOG("strategy %s: 缺 source", name);
            continue;
        }
        if (interval < 10 || interval > 3600000) {
            HMI_LOG("strategy %s: interval_ms=%ld 越界（10..3600000）", name, interval);
            continue;
        }

        st = &s_items[s_count];
        memset(st, 0, sizeof(*st));
        snprintf(st->name, sizeof(st->name), "%s", name);

        if (lua_rt_run(src, "=<strategy>.source", err, sizeof(err)) != 0) {
            HMI_LOG("strategy %s: 源码执行失败：%s", name, err);
            continue;
        }
        if (lua_rt_ref_global_fn("timer", &st->ref, err, sizeof(err)) != 0) {
            HMI_LOG("strategy %s: %s", name, err);
            continue;
        }
        if (cond && *cond) {
            if (expr_compile(cond, &st->cond, err, sizeof(err)) != 0) {
                HMI_LOG("strategy %s: condition 编译失败：%s", name, err);
                continue;
            }
            st->has_cond = 1;
        }
        st->timer = lv_timer_create(strat_cb, (uint32_t)interval, st);
        if (!st->timer) {
            HMI_LOG("strategy %s: 定时器创建失败", name);
            continue;
        }
        s_count++;
        HMI_LOG("strategy %s: 每 %ldms 执行一次%s", name, interval,
                st->has_cond ? "（带条件）" : "");
    }

    hmi_json_free(arr);
    free(text);
    return 0;
}

void strategies_stop(void)
{
    for (int i = 0; i < s_count; i++) {
        if (s_items[i].timer) lv_timer_delete(s_items[i].timer);
        if (s_items[i].has_cond) expr_free(&s_items[i].cond);
        s_items[i].timer = NULL;
    }
    s_count = 0;
}
