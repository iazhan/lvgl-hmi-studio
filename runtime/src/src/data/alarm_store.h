/*
 * alarm_store.h — 报警事件文件（ADR-0008 决定 5）
 *
 * 每个报警组一个环形文件 <data_dir>/alarms/alm_<hash>.bin：
 *   头 32 字节：magic "ALM1" | ver u16 | rec_size u16 | rsv u16
 *               max_rec u32 | head u32 | count u32 | group_hash u32 | rsv
 *   记录 12 字节：ts u32 | rule_id u16 | event u8 | level u8 | value f32
 *
 * 文件名用组名的 FNV-1a 哈希（组名可能是中文，直接做文件名在 FAT 上不可靠），
 * 头里再存一份哈希用于自检。
 */
#ifndef HMI_ALARM_STORE_H
#define HMI_ALARM_STORE_H

#include "core/alarm.h"

/* 事件文件根目录（其下建 alarms/）；不设则用 /data/chenge */
void alarm_store_set_dir(const char* dir);

/* 追加一条事件（组不存在就建）。返回 0 成功。 */
int  alarm_store_append(const char* group, const alarm_event_t* ev);

/* 读事件，新的在前；from/to 是 unix 秒（0 = 不限）。返回条数或 -1。 */
int  alarm_store_read(const char* group, uint32_t from, uint32_t to,
                      alarm_event_t* out, int max);

#endif /* HMI_ALARM_STORE_H */
