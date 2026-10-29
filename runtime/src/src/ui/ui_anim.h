/*
 * ui_anim.h — 页面级属性动画（46 §4）
 *
 * 契约是页面里的 animations[]：每条描述"某个控件的某个属性从 from 到 to"，
 * 由 LVGL 的 lv_anim 驱动（路径曲线也用 LVGL 自带的，不自己写浮点——ARMv5 无 FPU）。
 *
 * 优先级：anim（瞬时）> states > bind > props。动画结束或被新的动画打断时，
 * 把属性还原成建页时记下的原值，随后 states/bind 会在下一次刷新里重新生效。
 */
#ifndef HMI_UI_ANIM_H
#define HMI_UI_ANIM_H

#include "lvgl.h"
#include "widgets.h"

#include <stddef.h>

/* 每页最多多少条（46 §4 定的上限） */
#define UI_ANIM_MAX 64

/* 建表：target 按 id 找控件；返回 0 成功，失败写 err */
int  ui_anim_build(const hmi_json_t* arr, widget_t* ws,
                   const hmi_json_t* const* nodes, int n,
                   char* err, size_t errsz);

/* trigger=load 的那批，建页收尾时启动 */
void ui_anim_start_load(void);

/* 表达式触发的沿检测，由刷新定时器每拍调用 */
void ui_anim_tick(void);

/* 拆页：停掉所有动画并清表 */
void ui_anim_reset(void);

#endif /* HMI_UI_ANIM_H */
