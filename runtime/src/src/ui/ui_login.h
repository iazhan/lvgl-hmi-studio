/*
 * ui_login.h — 内置登录框（ADR-0012）
 *
 * 用户名列表 + 密码（走通用文本键盘）。免密用户点名字即登录。
 * 登录成功后重建当前页，让控件按新身份重算可见与禁用。
 */
#ifndef HMI_UI_LOGIN_H
#define HMI_UI_LOGIN_H

/* 打开登录框；没有启用用户表或已登录时是空操作 */
void login_dialog_open(void);

/* 关掉登录框（取消） */
void login_dialog_close(void);

/* 拆页：丢掉对象指针（对象随页面删除） */
void login_dialog_reset(void);

#endif /* HMI_UI_LOGIN_H */
