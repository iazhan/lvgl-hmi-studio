/*
 * auth.h — 会话与权限判定（ADR-0012 / ADR-0026）
 *
 * 会话只在内存：登录后记用户名、组与组对应的命名权限位，重启失效。
 * 超时两条：idle_min（空闲）与 session_timeout_min（登录后总时长），任一到期就登出。
 * 没有 users.json 时整个权限体系不启用（auth_enabled() == 0，一律放行）。
 *
 * 权限分两层（见 core/perm.h）：组掩码管控件显隐/禁用，命名位管动作执行。
 */
#ifndef HMI_AUTH_H
#define HMI_AUTH_H

#include <stddef.h>
#include <stdint.h>

#include "core/perm.h"
#include "data/user_store.h"

/* 安全策略：manifest 里的可选字段（37 §6.9），由 loader 夹到合法区间 */
typedef struct {
    int idle_min;             /* 空闲多少分钟自动登出，0 = 不超时 */
    int session_timeout_min;  /* 登录后最长会话时长，0 = 不限 */
    int max_login_attempts;   /* 同一用户连续失败多少次锁定，1..10 */
    int lockout_min;          /* 锁定分钟数，0 = 不锁 */
    const char* default_user; /* 启动自动登录的用户名（须免密），可 NULL */
} auth_cfg_t;

int  auth_init(const auth_cfg_t* cfg);
int  auth_enabled(void);                        /* 0 = 没有用户表，不启用 */

/* 登录：0 成功、-1 用户名或口令错、-2 失败次数过多锁定中、-3 未启用 */
int  auth_login(const char* name, const char* pwd);
void auth_logout(void);
void auth_touch(void);                          /* 有用户操作时调 */
void auth_tick(void);                           /* 主循环每 100ms 调 */

const char* auth_user_name(void);               /* NULL = 未登录 */
const char* auth_user_groups(void);             /* 组名逗号分隔（审计用），未登录 "" */
int  auth_is_admin(void);
int  auth_logged_in(void);

/* 命名权限位是否生效：只有 users.json 里至少一个组声明了 perms[] 才生效。
 * 不生效时 auth_perm() 一律放行——老工程（只有 perm 组掩码）的现场语义不变。 */
int  auth_named_perms(void);

/* 当前用户是否拥有这些命名位（bit 可组合，要求全部满足）。
 * 未登录 = 没有；管理员组 = 全部。 */
int  auth_perm(perm_bits_t bit);

/* 当前用户是否落在 perm 组掩码里；mask=0 表示不限（未登录也放行）。
 * 未登录且 mask 非 0 → 0。 */
int  auth_can(perm_mask_t mask);

int  auth_lockout_min(void);                    /* 登录框提示用 */
int  auth_max_attempts(void);

#endif /* HMI_AUTH_H */
