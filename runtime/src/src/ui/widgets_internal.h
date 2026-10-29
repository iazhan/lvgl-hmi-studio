/*
 * widgets_internal.h — 控件族之间的内部接口
 *
 * 只给 src/ui 内部用：每种控件的 build_* 由 widgets.c 的分发调用，控件之间再共用
 * widgets_common.c 里的坐标/颜色/文本样式工具。对外 API 只有 widgets.h 里那些 ui_*。
 *
 * 新增控件：在这里加一行 build_ 原型，在 widgets.c 的 widget_build() 里加分支，
 * 实现放进对应的 widget_*.c（按族分文件，见文件头注释）。
 */
#ifndef HMI_WIDGETS_INTERNAL_H
#define HMI_WIDGETS_INTERNAL_H

#include "widgets.h"

/* ── widgets_common.c 提供 ── */
lv_obj_t*    cur_parent(void);                 /* 当前建控件的父对象（容器里建子控件时用） */
void         place(lv_obj_t* o, long x, long y, long ww, long hh);
void         apply_text_style(lv_obj_t* obj, const hmi_json_t* props, int def_fs);

/* 应用 SC_COMMON 里的外观属性（bg_color / border_color / border_width / radius），
 * 有则覆盖、无则不动。原本硬编码或强制透明的控件（label/input/combobox/roller）
 * 要靠它让编辑器里设的颜色真正生效。 */
void         apply_box_style(lv_obj_t* obj, const hmi_json_t* props);

/* 建页收口（widget_build 调）：上面四个键 + opacity + visible。图元与 LED 例外，
 * 见实现注释。 */
void         apply_common_style(widget_t* w, const hmi_json_t* props);

/* 「上色的那个对象」：lamp 是透明容器 + 灯珠两级，颜色在灯珠上（容器只接几何补丁）；
 * 其它类型就是 obj 本身。绑定/states 改颜色一律经它，否则灯会"改了没反应"。 */
lv_obj_t*    widget_paint_obj(const widget_t* w);
void         apply_text_valign(lv_obj_t* obj, const hmi_json_t* props, long hh, int def_fs);
const char*  widget_id(const hmi_json_t* w);   /* 报错文案用；缺 id 时返回 "?" */
int          widget_rect(const hmi_json_t* w, long* x, long* y, long* ww, long* hh);
extern const project_t* ui_proj_ctx;           /* ui_set_project() 存下的工程，查变量用 */
extern char             ui_asset_root[256];    /* 图片等资产的工程内根目录 */
const char* ui_trend_dir(void);                /* 趋势数据根（其下 trends/），曲线与历史表共用 */

/* ── widget_text.c ── */
int build_label(const hmi_json_t* w, const hmi_json_t* props,
                long x, long y, long ww, long hh, widget_t* out);
int build_value(const hmi_json_t* props,
                long x, long y, long ww, long hh, widget_t* out);
int build_enum_text(const hmi_json_t* w, const hmi_json_t* props,
                    long x, long y, long ww, long hh, widget_t* out);
int build_ascii_display(const hmi_json_t* props, long x, long y, long ww, long hh,
                        widget_t* out);
int build_message(const hmi_json_t* props, long x, long y, long ww, long hh,
                  widget_t* out, char* err, size_t errsz);

/* ── widget_action.c ── */
int build_button(const hmi_json_t* w, const hmi_json_t* props,
                 long x, long y, long ww, long hh, widget_t* out);
int build_switch(const hmi_json_t* props, long x, long y, long ww, long hh,
                 widget_t* out);
int build_checkbox(const hmi_json_t* props, long x, long y, long ww, long hh,
                   widget_t* out);
int build_keyboard(const hmi_json_t* props, long x, long y, long ww, long hh,
                   widget_t* out);
int build_tabview(const hmi_json_t* props, long x, long y, long ww, long hh,
                  widget_t* out, char* err, size_t errsz);
int build_slider(const hmi_json_t* props, long x, long y, long ww, long hh,
                 widget_t* out);

/* ── widget_indicator.c ── */
int build_lamp(const hmi_json_t* w, const hmi_json_t* props,
               long x, long y, widget_t* out);
int build_bar(const hmi_json_t* props, long x, long y, long ww, long hh,
              widget_t* out);
int build_gauge(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out);
int build_pipe(const hmi_json_t* props, long x, long y, long ww, long hh,
               widget_t* out);
int build_spinner(const hmi_json_t* props, long x, long y, long ww, long hh,
                  widget_t* out);
int build_knob(const hmi_json_t* props, long x, long y, long ww, long hh,
               widget_t* out);

/* ── widget_input.c ── */
int build_input(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out, char* err, size_t errsz);
int build_combobox(const hmi_json_t* props, long x, long y, long ww, long hh,
                   widget_t* out);
int build_roller(const hmi_json_t* props, long x, long y, long ww, long hh,
                 widget_t* out);

/* ── widget_image.c ── */
int build_image(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out, char* err, size_t errsz);
int build_state_image(const hmi_json_t* props, long x, long y, long ww, long hh,
                      widget_t* out, char* err, size_t errsz);
int build_frame_anim(const hmi_json_t* props, long x, long y, long ww, long hh,
                     widget_t* out, char* err, size_t errsz);
int build_qrcode(const hmi_json_t* props, long x, long y, long ww, long hh,
                 widget_t* out, char* err, size_t errsz);

/* ── widget_table.c ── */
int build_table(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out, char* err, size_t errsz);

/* ── widget_history_table.c ── */
int build_history_table(const hmi_json_t* props, long x, long y, long ww, long hh,
                        widget_t* out, char* err, size_t errsz);

/* ── widget_curve.c（三条曲线共用一份建法） ── */
int build_curve(const hmi_json_t* props, long x, long y, long ww, long hh,
                widget_t* out, char* err, size_t errsz, int is_history);
int build_realtime_curve(const hmi_json_t* props, long x, long y, long ww, long hh,
                         widget_t* out, char* err, size_t errsz);
int build_xy_curve(const hmi_json_t* props, long x, long y, long ww, long hh,
                   widget_t* out, char* err, size_t errsz);

/* ── widget_states.c（states 要在控件建好之后解析：先捕获基础外观） ── */
int  wstate_parse(widget_t* w, const hmi_json_t* props, char* err, size_t errsz);
void wstate_capture(widget_t* w);

#endif /* HMI_WIDGETS_INTERNAL_H */
