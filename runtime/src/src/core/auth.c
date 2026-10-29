/*
 * auth.c — 会话与权限判定（ADR-0012 / ADR-0026）
 *
 * 会话只在内存；登录失败按用户计数（用户名不存在的尝试归到同一个桶），
 * 到上限锁 lockout_min 分钟，0 = 只记日志不锁。
 * 空闲超时只清会话，由调用方（main 循环）决定要不要重建页面。
 */
#include "auth.h"
#include "audit.h"
#include "hmi_log.h"

#include <stdio.h>
#include <string.h>
#include <time.h>

static int         s_enabled;
static int         s_named;            /* 1 = 命名位生效（用户表里有 perms[]） */
static int         s_idle_min;
static int         s_session_min;
static int         s_max_attempts;
static int         s_lock_min;
static perm_mask_t s_user_mask;        /* 当前用户的组掩码，登录时算好 */
static perm_bits_t s_user_bits;        /* 当前用户的命名位 */
static char        s_groups_str[96];   /* 组名逗号列表（审计写 groups=） */
static int         s_ui = -1;          /* 当前用户下标，-1 = 未登录 */
static time_t      s_last;             /* 最后一次操作时刻（空闲超时） */
static time_t      s_login_at;         /* 登录时刻（绝对会话上限） */

/* 失败计数按用户下标；最后一个槽收"用户名不存在"的尝试 */
static int    s_fails[USER_MAX_USERS + 1];
static time_t s_lock_until[USER_MAX_USERS + 1];

static void session_clear(void)
{
    s_ui = -1;
    s_user_mask = 0;
    s_user_bits = 0;
    s_groups_str[0] = '\0';
    s_last = time(NULL);
}

int auth_init(const auth_cfg_t* cfg)
{
    s_enabled = user_count() > 0;
    s_idle_min = cfg ? cfg->idle_min : 10;
    s_session_min = cfg ? cfg->session_timeout_min : 0;
    s_max_attempts = (cfg && cfg->max_login_attempts > 0) ? cfg->max_login_attempts : 3;
    s_lock_min = cfg ? cfg->lockout_min : 1;
    s_ui = -1;
    s_user_mask = 0;
    s_user_bits = 0;
    s_groups_str[0] = '\0';
    s_last = time(NULL);
    s_login_at = 0;
    memset(s_fails, 0, sizeof(s_fails));
    memset(s_lock_until, 0, sizeof(s_lock_until));

    s_named = 0;
    for (int i = 0; i < user_group_count(); i++)
        if (user_group_at(i)->has_perms) { s_named = 1; break; }

    if (!s_enabled) {
        HMI_LOG("auth: 没有 users.json，权限未启用（所有动作放行）");
        return 0;
    }
    HMI_LOG("auth: 启用权限，%d 组 %d 用户，空闲超时 %d 分钟，会话上限 %d 分钟，"
            "失败 %d 次锁 %d 分钟",
            user_group_count(), user_count(), s_idle_min, s_session_min,
            s_max_attempts, s_lock_min);
    for (int i = 0; i < user_group_count(); i++) {
        const user_group_t* g = user_group_at(i);
        char text[160];

        if (g->admin) {
            snprintf(text, sizeof(text), "全部（管理员组）");
        } else {
            perm_bits_text(g->perms, text, sizeof(text));
        }
        HMI_LOG("auth: 组 %s 命名位 %s", g->id, text);
    }
    if (!s_named)
        HMI_LOG("auth: 没有组声明 perms[]，命名权限位不生效（只按 perm 组掩码判）");

    if (cfg && cfg->default_user && *cfg->default_user) {
        int ui = user_find(cfg->default_user);

        if (ui >= 0 && user_at(ui)->pass[0] == '\0') {
            (void)auth_login(cfg->default_user, NULL);
            HMI_LOG("auth: 按 manifest.default_user 自动登录 %s", cfg->default_user);
        } else {
            HMI_LOG("auth: default_user=%s 不存在或需要口令，保持未登录", cfg->default_user);
        }
    }
    return 0;
}

int auth_enabled(void)
{
    return s_enabled;
}

int auth_named_perms(void)
{
    return s_named;
}

int auth_logged_in(void)
{
    return s_ui >= 0;
}

int auth_login(const char* name, const char* pwd)
{
    time_t now = time(NULL);
    int ui;
    int slot;

    if (!s_enabled) return -3;
    ui = user_find(name);
    slot = (ui >= 0) ? ui : USER_MAX_USERS;
    if (s_lock_min > 0 && s_lock_until[slot] > now) {
        HMI_LOG("auth: %s 锁定中（还剩 %ld 秒）", name ? name : "(空)",
                (long)(s_lock_until[slot] - now));
        return -2;
    }
    if (ui < 0 || user_verify(ui, pwd) != 0) {
        s_fails[slot]++;
        if (s_lock_min > 0 && s_fails[slot] >= s_max_attempts) {
            s_lock_until[slot] = now + (time_t)s_lock_min * 60;
            s_fails[slot] = 0;
            HMI_LOG("auth: %s 连续失败 %d 次，锁定 %d 分钟",
                    name ? name : "(空)", s_max_attempts, s_lock_min);
            audit_log("login-locked", name, "连续失败 %d 次，锁 %d 分钟",
                      s_max_attempts, s_lock_min);
        } else {
            HMI_LOG("auth: 登录失败（%s）第 %d 次", name ? name : "(空)", s_fails[slot]);
            audit_log("login-fail", name, "第 %d 次", s_fails[slot]);
        }
        return -1;
    }
    s_fails[slot] = 0;
    s_lock_until[slot] = 0;
    s_ui = ui;
    s_last = now;
    s_login_at = now;
    s_user_mask = 0;
    s_user_bits = 0;
    s_groups_str[0] = '\0';
    {
        const user_t* u = user_at(ui);

        for (int i = 0; i < u->ngroups; i++) {
            int gi = user_group_find(u->groups[i]);
            size_t used = strlen(s_groups_str);
            size_t left = sizeof(s_groups_str) - used;

            if (gi < 0) continue;
            if (gi < 8) s_user_mask |= (perm_mask_t)(1u << gi);
            if (user_group_at(gi)->admin) s_user_bits |= (perm_bits_t)PERM_ALL;
            s_user_bits |= user_group_at(gi)->perms;
            snprintf(s_groups_str + used, left, "%s%s", used ? "," : "", u->groups[i]);
        }
    }
    HMI_LOG("auth: 登录成功 %s（组 %s）", user_at(ui)->name,
            s_groups_str[0] ? s_groups_str : "-");
    audit_log("login-ok", user_at(ui)->name, NULL);
    return 0;
}

void auth_logout(void)
{
    if (s_ui >= 0) {
        HMI_LOG("auth: 登出 %s", user_at(s_ui)->name);
        audit_log("logout", user_at(s_ui)->name, NULL);
    }
    session_clear();
}

void auth_touch(void)
{
    s_last = time(NULL);
}

void auth_tick(void)
{
    time_t now = time(NULL);

    if (!s_enabled || s_ui < 0) return;
    if (s_idle_min > 0 && now - s_last >= (time_t)s_idle_min * 60) {
        HMI_LOG("auth: 空闲 %d 分钟，自动登出", s_idle_min);
        audit_log("logout-idle", user_at(s_ui)->name, "空闲 %d 分钟", s_idle_min);
        session_clear();
        return;
    }
    if (s_session_min > 0 && now - s_login_at >= (time_t)s_session_min * 60) {
        HMI_LOG("auth: 会话已达上限 %d 分钟，自动登出", s_session_min);
        audit_log("logout-timeout", user_at(s_ui)->name, "会话上限 %d 分钟", s_session_min);
        session_clear();
        /* 调用方看到 auth_logged_in() 变假后自行重建页面 */
    }
}

const char* auth_user_name(void)
{
    const user_t* u = user_at(s_ui);

    return u ? u->name : NULL;
}

const char* auth_user_groups(void)
{
    return s_groups_str;
}

int auth_is_admin(void)
{
    const user_t* u = user_at(s_ui);

    if (!u) return 0;
    for (int i = 0; i < u->ngroups; i++) {
        int gi = user_group_find(u->groups[i]);

        if (gi >= 0 && user_group_at(gi)->admin) return 1;
    }
    return 0;
}

int auth_perm(perm_bits_t bit)
{
    if (!s_enabled) return 1;                      /* 没启用 = 不拦 */
    if (!s_named) return 1;                        /* 老表没有命名位 = 只按组掩码 */
    if (!bit) return 1;
    if (s_ui < 0) return 0;                        /* 命名位生效而未登录 = 不放行 */
    if (auth_is_admin()) return 1;
    return (bit & s_user_bits) == bit;
}

int auth_can(perm_mask_t mask)
{
    if (!s_enabled) return 1;                      /* 没启用 = 不拦 */
    if (mask == 0) return 1;                       /* 没写 perm = 不限 */
    if (s_ui < 0) return 0;                        /* 有 perm 且未登录 = 不放行 */
    if (auth_is_admin()) return 1;
    return (mask & s_user_mask) != 0;
}

int auth_lockout_min(void)
{
    return s_lock_min;
}

int auth_max_attempts(void)
{
    return s_max_attempts;
}
