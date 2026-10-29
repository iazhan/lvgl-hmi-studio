/*
 * audit.h — 审计日志（ADR-0026）
 *
 * 只记**安全与操作事件**（登录/登出/锁定/改密/拒权/写变量/配方/报警/存盘），
 * 不记调试输出——后者走 HMI_LOG。分流的原因：审计要能事后追责，所以它落盘、
 * 带墙钟时间戳、且不受任何日志级别影响；调试输出掉电即弃。
 *
 * 落点 `<日志目录>/audit.log`：板端是 /data/userdata/logs（与 launcher、维护页
 * 的日志空间/清空、U 盘"收集日志"同一处），目录不可写时退到 <数据目录>/logs；
 * 宿主与 golden 用 HMI_AUDIT_DIR 直接指定，免得把测试条目写进真实数据卷。
 */
#ifndef HMI_AUDIT_H
#define HMI_AUDIT_H

#include <stddef.h>

#define AUDIT_MAX_BYTES (64L * 1024L)   /* 滚动上限：一代，滚成 .1；审计要的是"最近发生过什么" */

/* 启动时定落点（data_dir 是运行时数据根，用于回退） */
void audit_init(const char* data_dir);

const char* audit_path(void);           /* 实际落点，未初始化/不可写时为 "" */

/* 记一条事件。user 传 NULL = 用当前会话身份；detail_fmt 可 NULL。
 * 同时写串口（带 AUDIT 前缀），落盘失败时这条记录不会凭空消失。 */
void audit_log(const char* event, const char* user, const char* detail_fmt, ...);

/* 拒绝：动作名 + 原因。界面 toast 之外必须留痕，否则"点了没反应"无从追查。 */
void audit_denied(const char* what, const char* why);

/* 写变量：同一秒内的多条合并成一条（滑块拖动是 10Hz，逐条记会把 64KB 在
 * 十几分钟内写满），下一次写得出来时附带"另有 N 次"的计数。 */
void audit_tag_write(const char* tag_name, const char* value_text);

#endif /* HMI_AUDIT_H */
