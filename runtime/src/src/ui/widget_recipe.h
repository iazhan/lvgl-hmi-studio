/*
 * widget_recipe.h — 配方表（ADR-0009）
 *
 * 行 = 一条配方，列 = name / desc / 成员值。点成员值列弹数字键盘（ADR-0010），
 * 点名字列弹 ASCII 文本键盘；点其它位置 = 下载该配方并设为当前。
 */
#ifndef HMI_WIDGET_RECIPE_H
#define HMI_WIDGET_RECIPE_H

#include "widgets.h"

int  recipe_table_build(const hmi_json_t* props, long x, long y, long ww, long hh,
                        widget_t* out, char* err, size_t errsz);

/* 重新读一遍内存里的配方数据并重填表格（下载/新建/改名之后调用） */
void recipe_table_refresh(widget_t* w);

/* 拆页：释放控件私有数据（非配方表直接返回） */
void recipe_widget_dispose(widget_t* w);

#endif /* HMI_WIDGET_RECIPE_H */
