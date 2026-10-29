/*
 * widget_chart.h — 柱 / 饼 / 环 图表
 *
 * 数值来自 series[i].value 的 {tag|expr}，运行时按 500ms 重算；
 * 形状画在 DRAW_MAIN 回调里（扇形复用图元的抗锯齿填充），
 * 数值与图例用子标签，刷新时一起挪位置。
 */
#ifndef HMI_WIDGET_CHART_H
#define HMI_WIDGET_CHART_H

#include "widgets.h"

/* 按 chart 控件的 props 建对象；属性不合法返回 -1。 */
int chart_build(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out, char* err, size_t errsz);

/* 拆页时调用：停掉刷新定时器并释放图表自己的数据。 */
void chart_dispose(widget_t* w);

#endif /* HMI_WIDGET_CHART_H */
