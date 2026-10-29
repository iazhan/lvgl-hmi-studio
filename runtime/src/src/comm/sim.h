/*
 * sim.h — 模拟设备：小内存模型 + 周期生成值
 *
 * 对应 37 §4 的 sim 设备："sim 使用同一套内存模型"——holding 为 16 位寄存器
 * 数组，coil 为位数组，32/64 位数据按 default_order（缺省 ABCD 大端）占
 * 连续寄存器。v1 只有这一个协议，Modbus 落地时它的帧编解码可复用同一套
 * 地址/类型参数。
 */
#ifndef HMI_SIM_H
#define HMI_SIM_H

#include <stdint.h>

#include "project/loader.h"   /* AREA_* / DT_* */

#define SIM_HOLDING_MAX 1024   /* 寄存器数；g01 只用到 addr 100 */
#define SIM_COIL_MAX    256    /* 线圈数 */

typedef struct {
    uint16_t holding[SIM_HOLDING_MAX];
    uint8_t  coil[(SIM_COIL_MAX + 7) / 8];
} sim_dev_t;

void sim_init(sim_dev_t* s);

/* 按经过的毫秒推进设备侧生成值；调用周期由调用方决定（主循环用设备
 * poll_ms），生成规则自己的周期在 sim.c 的生成器表里。 */
void sim_tick(sim_dev_t* s, uint32_t elapsed_ms);

/* 读一个变量当前值到 num（数值型）或 boolean（bool 型）。
 * 地址/类型不匹配返回 -1。discrete 与 coil 共用位数组、input 与 holding 共用
 * 寄存器数组（37 §5「sim 使用同一套内存模型」）。 */
int  sim_read(const sim_dev_t* s, int area, int addr, int datatype,
              double* num, int* boolean);

/* 写一个变量值。只读区/越界/类型不符返回 -1。 */
int  sim_write(sim_dev_t* s, int area, int addr, int datatype,
               double num, int boolean);

/* 把 sim 内存模型同步进 Tag DB（主循环 1s 定时器调用） */
void sim_sync_tags(const sim_dev_t* s, const tag_def_t* tags, int count);

/* 把一次变量写请求送达 sim 设备（tagdb 写成功之后调用）。
 * 该变量没有 sim source 时是空操作，返回 0。 */
int  sim_write_tag(sim_dev_t* s, const tag_def_t* t, double num, int boolean);

/* 字符串写：按 2 字节/寄存器铺进 holding（与 sim_sync_tags 读回的顺序一致），
 * 不足 src_len 的部分补 0。没有 sim source 时是空操作。 */
int  sim_write_str(sim_dev_t* s, const tag_def_t* t, const char* bytes, int n);

#endif /* HMI_SIM_H */
