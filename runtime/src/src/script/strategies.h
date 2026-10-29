/*
 * strategies.h — 定时策略（37 §7.4）
 *
 * 文件：<工程根>/strategies.json，数组，每项
 *   { "name": "batch", "enabled": true, "trigger": "timer",
 *     "interval_ms": 100, "condition": "", "source": "function timer() ... end" }
 *
 * 每个周期先求 condition（37 §6.3 的 expr 语法，空表示恒真），成立才调 timer()。
 * 连续 3 次报错停用该策略——与页面 on_timer 同一条规矩。
 */
#ifndef HMI_STRATEGIES_H
#define HMI_STRATEGIES_H

#include "project/loader.h"

/* 读 strategies.json 并起定时器。文件不存在视为没有策略，返回 0。 */
int  strategies_start(const project_t* p);

/* 停掉所有策略定时器并释放引用（关工程时调用）。 */
void strategies_stop(void);

#endif /* HMI_STRATEGIES_H */
