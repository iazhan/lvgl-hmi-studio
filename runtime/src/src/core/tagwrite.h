/*
 * tagwrite.h — 写变量：本地缓存 + 下发到设备
 *
 * 只写 tagdb 是不够的：变量若绑定设备，下一轮轮询会把它覆盖回设备里的旧值
 * （配方下载 30 → 一秒后变回 0 就是这么来的）。所以"写变量"必须三件事一起做：
 * 改本地值、写 sim 寄存器、或把请求投进 modbus 写队列。
 *
 * 界面动作、配方下载、脚本 cg.write 都走这里，避免各写一套。
 */
#ifndef HMI_TAGWRITE_H
#define HMI_TAGWRITE_H

#include "project/loader.h"
#include "comm/sim.h"

/* 启动时挂一次上下文（工程 + 模拟设备）；重复调用以最后一次为准 */
void tagwrite_init(const project_t* p, sim_dev_t* sim);

/* 写数值 / 布尔：成功 0，只读或类型不符 -1 */
int  tagwrite_num(int tag, double v);
int  tagwrite_bool(int tag, int b);

/* 写字符串（只能写 string 变量）：本地值 + 设备下发（sim 寄存器 / modbus fc16） */
int  tagwrite_str(int tag, const char* s);

#endif /* HMI_TAGWRITE_H */
