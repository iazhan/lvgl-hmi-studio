/*
 * ui_page_internal.h — 页面三件套（生命周期 / 交互回调 / 动作）之间的内部接口
 *
 * ui_page.c 持有页面级状态（控件数组、动作数组、工程上下文），拆页即整页销毁；
 * ui_page_actions.c 往动作数组里登记并执行，ui_page_input.c 的控件回调用同一份
 * 控件数组反查 widget。对外 API 只有 ui_page.h。
 */
#ifndef HMI_UI_PAGE_INTERNAL_H
#define HMI_UI_PAGE_INTERNAL_H

#include "ui_page.h"
#include "core/perm.h"

/* ── ui_page.c 持有、另两个文件共用 ── */
extern const project_t* s_proj;
extern sim_dev_t*       s_sim;
extern widget_t         s_widgets[WIDGET_MAX_PER_PAGE];
extern int              s_nwidgets;
extern page_action_t    s_actions[WIDGET_MAX_PER_PAGE];
extern int              s_nactions;
/* 当前页的权限组掩码：脚本写变量/写设备要与页面权限同源（ADR-0026） */
extern perm_mask_t      s_page_view;
extern perm_mask_t      s_page_oper;

/* 按 LVGL 对象反查控件（事件回调里用）；找不到返回 NULL */
widget_t* widget_by_obj(lv_obj_t* obj);

/* ── ui_page_actions.c 提供 ── */
int  parse_actions(const hmi_json_t* ev, const char* wid, char* err, size_t errsz);
void action_cb(lv_event_t* e);
void page_run_action(long idx);           /* 权限 + 分派；长按与确认框直接调它 */
int  apply_tag_write(int tag_id, int is_bool, int b, double n);

/* ── ui_page_input.c 提供（build_page 挂事件、拆页收尾时要用） ── */
void input_click_cb(lv_event_t* e);
void writeback_cb(lv_event_t* e);
/* keyboard 控件：把内联键盘挂到 target 上（提交路径与点开 input 一致） */
int  keyboard_attach(widget_t* kb, widget_t* target, char* err, size_t errsz);
void combo_change_cb(lv_event_t* e);
void toast_reset(void);

/* 长按（events.long_press）：build_page 把这三个回调挂在控件上 */
void tip_begin_cb(lv_event_t* e);         /* tooltip：LV_EVENT_PRESSED */
void tip_end_cb(lv_event_t* e);           /* tooltip：RELEASED / PRESS_LOST / DELETE */
void hold_begin_cb(lv_event_t* e);        /* LV_EVENT_PRESSED */
void hold_end_cb(lv_event_t* e);          /* RELEASED / PRESS_LOST / DELETE */

/* 二次确认（button props.confirm）：action_cb 在 click 时弹框，拆页时收尾 */
void confirm_open(widget_t* w, long slot);
void confirm_reset(void);

#endif /* HMI_UI_PAGE_INTERNAL_H */
