/*
 * trend_logger.h — 趋势采集（把变量按时落盘，供 history_curve 回看）
 *
 * 配置：<工程根>/trends.json，数组，每项一组
 *   { "id": "furnace", "interval_ms": 1000, "max_records": 6000, "channels": [0, 2] }
 *
 * 每组一个采集线程：每 interval_ms 读一次变量表、追加一条记录。写失败只记日志，
 * 不中断采集（NAND 偶发错误不该让整个界面停下来）。
 */
#ifndef HMI_TREND_LOGGER_H
#define HMI_TREND_LOGGER_H

#include "project/loader.h"

/* 启动所有组；data_dir 是根（其下建 trends/）。没有 trends.json 返回 0。 */
int  trend_logger_start(const project_t* p, const char* data_dir);
void trend_logger_stop(void);

/* 取某组的通道数与各通道的 tag（历史曲线按 tag 找通道号）。找不到返回 -1。 */
int  trend_logger_group(const char* id, int* channels, const int** tags);

#endif /* HMI_TREND_LOGGER_H */
