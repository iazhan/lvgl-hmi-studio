/*
 * trend_store.h — 趋势数据文件（历史曲线的落盘格式）
 *
 * 固定记录长度的环形文件，写满就覆盖最旧的一条，不重写文件（NAND 上重写整文件代价太高）：
 *
 *   头 32 字节：magic "TDB1" | ver u16 | rec_size u16 | channels u8 | reserved u8
 *                interval_ms u32 | max_rec u32 | head u32 | count u32
 *   记录：ts u32（秒） + float values[channels]
 *
 * 只有 head/count 两个字段会在每次写入后更新（4KB 对齐的一小块，写在头部原位）。
 */
#ifndef HMI_TREND_STORE_H
#define HMI_TREND_STORE_H

#include <stdint.h>
#include <stddef.h>

#define TREND_MAGIC "TDB1"
#define TREND_HDR_SIZE 32
#define TREND_CH_MAX 8

typedef struct {
    int      fd;
    char     path[256];
    int      ch;
    int      max_rec;        /* 环形容量（条） */
    uint32_t interval_ms;
    uint32_t head;           /* 下一条写入位置 */
    uint32_t count;          /* 已有条数（≤ max_rec） */
    size_t   rec_size;
} trend_file_t;

typedef struct {
    uint32_t ts;
    float    v[TREND_CH_MAX];
} trend_rec_t;

/* 打开（不存在则创建）。truncate=1 时清空重建（配置变了要用）。 */
int  trend_open(trend_file_t* f, const char* dir, const char* id, int channels,
                int max_rec, int interval_ms, int truncate);
void trend_close(trend_file_t* f);

/* 追加一条；失败返回 -1（调用方记一次日志即可，不中断采集） */
int  trend_append(trend_file_t* f, uint32_t ts, const float* vals);

/* 读回全部记录（按时间从旧到新），*out 由调用方 free。返回条数或 -1。 */
int  trend_read_all(const char* dir, const char* id, trend_rec_t** out,
                    int* channels, int* interval_ms);

/* 只读头部（不打开文件也能判断是否存在/有多少条） */
int  trend_stat(const char* dir, const char* id, int* count, int* channels,
                uint32_t* interval_ms);

#endif /* HMI_TREND_STORE_H */
