/*
 * ui_figure.h — 图元绘制入口
 *
 * 37 种 kind 的几何与绘制都在 ui_figure.c；widgets.c 只管 shape 控件的外壳
 * （建对象、绑定、states），具体画什么交出去。
 */
#ifndef HMI_UI_FIGURE_H
#define HMI_UI_FIGURE_H

#include "widgets.h"

/* 按覆盖度抗锯齿填充一块闭合多边形（坐标相对对象左上角）。
 * 图元之外的控件（chart 的扇形/环扇）也用它，避免各自写一份填充。 */
void fig_fill_poly(lv_layer_t* layer, int ox, int oy,
                   const lv_point_precise_t* pts, int n,
                   lv_color_t color, lv_opa_t opa);

/* 按 shape 控件的 props 建图元对象；属性或尺寸不合法返回 -1。 */
int figure_build(const hmi_json_t* props, long x, long y, long ww, long hh,
                 widget_t* out);

#endif /* HMI_UI_FIGURE_H */
