/*
 * sysvar.c — 系统变量：时间/日期/用户/当前页/语言/报警计数
 *
 * 值缓存在本模块里，每秒 tick 一次并做变化检测；tagdb 的读入口遇到 60000+
 * 就转到这里，所以绑定、表达式、Lua 都把它们当普通变量读。
 */
#include "sysvar.h"
#include "core/alarm.h"
#include "core/auth.h"
#include "core/i18n.h"
#include "hmi_log.h"

#include <stdio.h>
#include <string.h>
#include <time.h>

typedef struct {
    int         id;
    const char* name;
    int         type;        /* tag_type_t */
} sysvar_def_t;

/* 预留段按 10 一组分块，留出后续插入的余地 */
static const sysvar_def_t s_defs[] = {
    { SYSVAR_BASE + 0,  "sys.time",     TAG_T_STRING },
    { SYSVAR_BASE + 1,  "sys.date",     TAG_T_STRING },
    { SYSVAR_BASE + 2,  "sys.week",     TAG_T_STRING },
    { SYSVAR_BASE + 3,  "sys.hour",     TAG_T_U16 },
    { SYSVAR_BASE + 4,  "sys.minute",   TAG_T_U16 },
    { SYSVAR_BASE + 5,  "sys.second",   TAG_T_U16 },
    { SYSVAR_BASE + 6,  "sys.year",     TAG_T_U16 },
    { SYSVAR_BASE + 7,  "sys.month",    TAG_T_U16 },
    { SYSVAR_BASE + 8,  "sys.day",      TAG_T_U16 },
    { SYSVAR_BASE + 9,  "sys.uptime",   TAG_T_U32 },   /* 开机秒数 */
    { SYSVAR_BASE + 10, "sys.user",     TAG_T_STRING },
    { SYSVAR_BASE + 11, "sys.scr",      TAG_T_STRING }, /* 当前页 id */
    { SYSVAR_BASE + 12, "sys.lang",     TAG_T_STRING },
    { SYSVAR_BASE + 13, "sys.alm",      TAG_T_U16 },    /* 活动报警数 */
    { SYSVAR_BASE + 14, "sys.alm_unack", TAG_T_U16 },   /* 未确认报警数 */
};

#define SYS_N ((int)(sizeof(s_defs) / sizeof(s_defs[0])))

typedef struct {
    char     s[24];
    double   n;
    uint32_t ver;
} sysvar_val_t;

static sysvar_val_t s_val[SYS_N];
static char         s_page[64];
static time_t       s_boot;

int sysvar_index(int id)
{
    if (id < SYSVAR_BASE) return -1;          /* 正常变量：一次比较就返回 */
    for (int i = 0; i < SYS_N; i++)
        if (s_defs[i].id == id) return i;
    return -1;
}

int sysvar_id_at(int i)
{
    return (i >= 0 && i < SYS_N) ? s_defs[i].id : -1;
}

const char* sysvar_name(int id)
{
    int i = sysvar_index(id);

    return i < 0 ? NULL : s_defs[i].name;
}

int sysvar_type(int id)
{
    int i = sysvar_index(id);

    return i < 0 ? -1 : s_defs[i].type;
}

double sysvar_num(int id)
{
    int i = sysvar_index(id);

    return (i < 0 || s_defs[i].type == TAG_T_STRING) ? 0.0 : s_val[i].n;
}

int sysvar_str(int id, char* out, int outsz)
{
    int i = sysvar_index(id);

    if (i < 0 || s_defs[i].type != TAG_T_STRING) return -1;
    if (!out || outsz <= 0) return -1;
    snprintf(out, (size_t)outsz, "%s", s_val[i].s);
    return (int)strlen(s_val[i].s);
}

uint32_t sysvar_version(int id)
{
    int i = sysvar_index(id);

    return i < 0 ? 0 : s_val[i].ver;
}

/* 值变了才递增 version：绑定靠它判断要不要重绘 */
static void put_num(int i, double v)
{
    if (s_val[i].n == v && s_val[i].ver != 0) return;
    s_val[i].n = v;
    s_val[i].ver++;
}

static void put_str(int i, const char* s)
{
    if (strcmp(s_val[i].s, s) == 0 && s_val[i].ver != 0) return;
    snprintf(s_val[i].s, sizeof(s_val[i].s), "%s", s);
    s_val[i].ver++;
}

void sysvar_set_page(const char* page_id)
{
    snprintf(s_page, sizeof(s_page), "%s", page_id ? page_id : "");
}

void sysvar_init(void)
{
    s_boot = time(NULL);
    memset(s_val, 0, sizeof(s_val));
    s_page[0] = '\0';
    sysvar_tick();
}

void sysvar_tick(void)
{
    static const char* const weeks[] = { "周日", "周一", "周二", "周三",
                                         "周四", "周五", "周六" };
    time_t now = time(NULL);
    struct tm tmv;
    char buf[40];

    if (now <= 0) return;
    localtime_r(&now, &tmv);

    snprintf(buf, sizeof(buf), "%02d:%02d:%02d", tmv.tm_hour, tmv.tm_min, tmv.tm_sec);
    put_str(0, buf);
    snprintf(buf, sizeof(buf), "%04d-%02d-%02d", tmv.tm_year + 1900, tmv.tm_mon + 1,
             tmv.tm_mday);
    put_str(1, buf);
    put_str(2, weeks[(tmv.tm_wday >= 0 && tmv.tm_wday < 7) ? tmv.tm_wday : 0]);

    put_num(3, tmv.tm_hour);
    put_num(4, tmv.tm_min);
    put_num(5, tmv.tm_sec);
    put_num(6, tmv.tm_year + 1900);
    put_num(7, tmv.tm_mon + 1);
    put_num(8, tmv.tm_mday);
    put_num(9, (double)(s_boot > 0 ? (long)(now - s_boot) : 0));

    put_str(10, auth_logged_in() ? auth_user_name() : "");
    put_str(11, s_page);
    put_str(12, i18n_lang() ? i18n_lang() : "");
    put_num(13, (double)alarm_active_count(NULL));
    put_num(14, (double)alarm_unacked_count(NULL));
}
