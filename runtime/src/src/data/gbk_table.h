/*
 * gbk_table.h — GBK 映射表的形状（生成物，勿手改，见 tools/gen_gbk_table.py）
 */
#ifndef HMI_GBK_TABLE_H
#define HMI_GBK_TABLE_H

#include <stdint.h>

#define GBK_LEAD_MIN  0x81
#define GBK_LEAD_MAX  0xFE
#define GBK_TRAIL_MIN 0x40
#define GBK_TRAIL_MAX 0xFE
#define GBK_TRAIL_COUNT 191
#define GBK_MAP_SIZE   24066

/* 0 = 该组合在 GBK 里没有字符；非 0 为 Unicode 码位（BMP，可直接转 3 字节 UTF-8） */
extern const uint16_t gbk_uni_map[GBK_MAP_SIZE];

#endif /* HMI_GBK_TABLE_H */
