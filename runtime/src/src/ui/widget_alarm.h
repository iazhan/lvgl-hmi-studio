/*
 * widget_alarm.h — 报警条 / 报警列表（ADR-0008）
 *
 * 两者共用报警引擎的活动表与事件文件，放在同一个文件里（共用的格式化与列渲染都在里面）。
 * 控件私有数据放 widget_t.cdata（堆上），拆页时由 alarm_widget_dispose() 释放。
 */
#ifndef HMI_WIDGET_ALARM_H
#define HMI_WIDGET_ALARM_H

#include "widgets.h"

int  alarm_bar_build(const hmi_json_t* props, long x, long y, long ww, long hh,
                     widget_t* out, char* err, size_t errsz);
int  alarm_list_build(const hmi_json_t* props, long x, long y, long ww, long hh,
                      widget_t* out, char* err, size_t errsz);

/* 拆页：停掉定时器并释放控件私有数据（非报警控件直接返回） */
void alarm_widget_dispose(widget_t* w);

#endif /* HMI_WIDGET_ALARM_H */
