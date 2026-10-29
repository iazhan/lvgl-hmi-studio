/*
 * sysvar.h — 系统变量（46 §8.4）
 *
 * 系统变量不进工程变量表：tag id 从 60000 起预留，读的时候由 tagdb 转到本模块，
 * 所以 expr 的 tag(id)、fmt 绑定、Lua cg.getTag 都自然可用，不需要各处特判。
 *
 * 只读：写入口（tagdb_write_*）对 60000+ 一律拒绝。
 */
#ifndef HMI_SYSVAR_H
#define HMI_SYSVAR_H

#include "core/tagdb.h"

#include <stddef.h>
#include <stdint.h>

#define SYSVAR_BASE 60000

/* 初始化（记启动时刻）并填一次值；可重复调用 */
void sysvar_init(void);

/* 每秒调一次：刷新时间/日期/用户/页面/语言/报警计数（内部做变化检测，
 * 没变就不递增 version，绑定不会白刷） */
void sysvar_tick(void);

/* 是系统变量就返回 0..n-1 的下标，否则 -1。正常变量走一次比较就返回 */
int  sysvar_index(int id);

/* 类型（tag_type_t）；未知返回 -1 */
int  sysvar_type(int id);

/* 数值型取值；字符串型返回 0 */
double sysvar_num(int id);

/* 字符串型取值：返回字节数，非字符串型返回 -1 */
int  sysvar_str(int id, char* out, int outsz);

/* 值变化的版本号（绑定靠它判断要不要重绘） */
uint32_t sysvar_version(int id);

/* 名字（"sys.time" 这类），给编辑器与日志用；未知返回 NULL */
const char* sysvar_name(int id);

/* 当前页 id（建页时设），$SYS$SCR 读它 */
void sysvar_set_page(const char* page_id);

/* 遍历（编辑器/调试用）：i 从 0 开始，越界返回 -1 */
int sysvar_id_at(int i);

#endif /* HMI_SYSVAR_H */
