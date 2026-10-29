/*
 * ui_keypad.h — 通用键盘（ADR-0010）
 *
 * 两种模式：
 *   KEYPAD_NUM  — 4×4 数字盘，提交一个 double（input 控件、配方数值单元格）
 *   KEYPAD_TEXT — 10 列 ASCII 盘（A-Z / 0-9 / . - _），提交一个字符串（配方名这类）
 *
 * 目标不是控件，而是「值 + 回调」；编辑缓冲在键盘自己的一行显示里，目标控件提交前不动，
 * 所以取消等于无副作用。文本模式只收 ASCII，中文靠组态里定义（见 ADR-0010 决定 6）。
 */
#ifndef HMI_UI_KEYPAD_H
#define HMI_UI_KEYPAD_H

#include "lvgl.h"

typedef enum {
    KEYPAD_NUM = 0,
    KEYPAD_TEXT
} keypad_mode_t;

typedef struct {
    keypad_mode_t mode;
    const char*   title;               /* 可空：顶部提示（一般是变量名 / 列名） */

    /* NUM 模式 */
    double        value;               /* 初值 */
    int           decimals;            /* 显示与提交的小数位（0..6） */
    int           has_min, has_max;
    double        min, max;            /* 提交前钳制 */
    void        (*on_commit_num)(void* ud, double v);

    /* TEXT 模式 */
    const char*   text;                /* 初值 */
    int           max_len;             /* 最大长度（1..24） */
    int           min_len;             /* 最短长度（0 = 不限）：在键盘里就挡，不放到回调里再退回来 */
    const char*   hint;                /* 打开时就显示的提示（弹窗被键盘压住时，原因只能写在这里） */
    void        (*on_commit_text)(void* ud, const char* s);

    /*
     * 关闭（取消 / X 键 / 失焦）时的回调：commit=blur 的输入框靠它写回。
     * 与 on_commit_* 的分工：那个是"用户按了确认"，这个是"这次输入结束了但没确认"。
     * 两者不会同时触发（确认路径会先置 s_committed 挡住它）。
     */
    void        (*on_close_num)(void* ud, double v);
    void        (*on_close_text)(void* ud, const char* s);

    void*         ud;
} keypad_req_t;

/* 键盘挂在哪个页面上（page_mgr 建完页 screen 后调用；拆页时随 screen 一起没了） */
void ui_keypad_set_screen(lv_obj_t* screen);

/* 打开键盘；返回 0 成功，-1 失败（没有 screen 或创建对象失败） */
int  ui_keypad_open(const keypad_req_t* req);

/* 内联键盘（keyboard 控件用）：键位建在 parent 里，不铺遮罩、不贴屏底。
 * 一页只允许一个（键盘状态是模块级单例）；已有一个在跑时返回 -1。 */
int  ui_keypad_open_inline(lv_obj_t* parent, const keypad_req_t* req);

/* 关掉键盘（取消，不提交） */
void ui_keypad_close(void);

/* 拆页：丢弃对象指针（对象本身随 screen 删除），清掉回调 */
void ui_keypad_reset(void);

#endif /* HMI_UI_KEYPAD_H */
