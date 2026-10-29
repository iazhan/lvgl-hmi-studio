/*
 * sim_cfg.h — 工程的模拟设备（ADR-0015）
 *
 * 两层，同时生效：
 *   <工程根>/sim.json  声明式生成器（斜坡/计数/循环 + 只读常量），先跑
 *   <工程根>/sim.lua   逻辑模拟（联锁/滞后/阀门→流量），后跑，以它为准
 * 都没有就不模拟（寄存器保持 0）。sim.c 只做寄存器模型与编解码，不再自带工程语义。
 */
#ifndef HMI_SIM_CFG_H
#define HMI_SIM_CFG_H

#include <stdint.h>

#include "project/loader.h"
#include "comm/sim.h"

/* 加载 sim.json / sim.lua 并初始化寄存器（常量、gens 的 init 值）。
 * 语法错误只跳过该层，返回 0 照常启动；两层的错误都写进 err（可空）。 */
int  sim_cfg_load(const project_t* p, sim_dev_t* sim, char* err, size_t errsz);

/* 主循环调用：内部按 tick_ms 节流（缺省 100ms），先生成器、再 on_tick */
void sim_cfg_tick(sim_dev_t* sim, uint32_t now_ms);

/* 上位机写变量之后回调 on_write(tag, value)（脚本没在跑就是空操作） */
void sim_cfg_on_write(int tag, double value);

/* 有没有任何一层在生效 */
int  sim_cfg_active(void);

/* 把累积的错误文案取走一次（启动后主循环用来提示一次，取完清空） */
const char* sim_cfg_take_error(void);

#endif /* HMI_SIM_CFG_H */
