/*
 * savecfg.h — 存盘组（37 §6.9 / ADR-0014）
 *
 * savecfg.json 的每组按 mode 采样：period（按周期）、change（值变化）、trigger（只由 save_now）。
 * 文件格式与 trends.json 的组完全相同（trends/<组名>.bin），历史控件因此能直接读。
 */
#ifndef HMI_SAVECFG_H
#define HMI_SAVECFG_H

#include <stddef.h>
#include <stdint.h>

#include "project/loader.h"

#define SAVECFG_MAX_GROUPS 8
#define SAVECFG_MAX_TAGS   32
#define SAVECFG_NAME_MAX   32

/* 加载 savecfg.json 并打开各组文件；没有该文件返回 0，失败 -1 */
int  savecfg_start(const project_t* p, const char* data_dir);
void savecfg_stop(void);

/* 主循环每 100ms 调：按 mode 决定是否落一条 */
void savecfg_tick(void);

/* 立即落一条：name 为空表示全部组。返回写入的条数，找不到组返回 -1 */
int  savecfg_sample_now(const char* name);

/* 历史控件查通道：找到返回 0，channels/tags 可空 */
int  savecfg_group(const char* id, int* channels, const int** tags);

/* 导出某组记录为 CSV 到 dir（文件名 <组名>_<YYYYmmdd_HHMMSS>.csv）。
 * last_min > 0 只导最近 N 分钟；from_ts/to_ts 非 0 时按绝对区间（unix 秒，含端点）。
 * 返回写出的数据行数（不含表头，0 = 区间内没记录），失败 -1 并写 err。 */
int  savecfg_export_csv(const char* name, int last_min, uint32_t from_ts, uint32_t to_ts,
                        const char* dir, char* err, size_t errsz);

int  savecfg_count(void);

#endif /* HMI_SAVECFG_H */
