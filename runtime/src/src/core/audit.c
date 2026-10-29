/*
 * audit.c — 审计日志（见 audit.h 的说明）
 *
 * 时间戳用**墙钟**（time/localtime）：审计是给人看的，tick 的起点是开机时刻，
 * 写进日志没人知道那是几点。会话超时另用墙钟差，见 auth.c。
 */
#include "audit.h"
#include "core/auth.h"
#include "hmi_log.h"
#include "hmi_paths.h"

#include <errno.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

static char   s_path[224];
static int    s_ready;
static time_t s_tag_sec;        /* 上一次记 tag-write 的秒 */
static int    s_tag_dropped;    /* 同一秒里被合并掉的条数 */

static void mkdir_p(const char* path)
{
    char tmp[224];
    size_t n;

    snprintf(tmp, sizeof(tmp), "%s", path);
    n = strlen(tmp);
    for (size_t i = 1; i < n; i++) {
        if (tmp[i] != '/') continue;
        tmp[i] = '\0';
        mkdir(tmp, 0755);
        tmp[i] = '/';
    }
    mkdir(tmp, 0755);
}

/* 能 append 打开才算可用：只 access(W_OK) 会漏掉"目录有而无文件写权限"的情形 */
static int usable(const char* path)
{
    FILE* f = fopen(path, "a");

    if (!f) return 0;
    fclose(f);
    return 1;
}

static int set_path(const char* dir)
{
    snprintf(s_path, sizeof(s_path), "%s/audit.log", dir);
    return usable(s_path);
}

void audit_init(const char* data_dir)
{
    const char* env = getenv("HMI_AUDIT_DIR");

    s_ready = 0;
    s_path[0] = '\0';
    s_tag_sec = 0;
    s_tag_dropped = 0;

    if (env && *env) {
        mkdir_p(env);
        s_ready = set_path(env);
    } else if (set_path(HMI_LOGS_DIR)) {
        s_ready = 1;
    } else if (data_dir && *data_dir) {
        char fb[200];

        snprintf(fb, sizeof(fb), "%s/logs", data_dir);
        mkdir_p(fb);
        s_ready = set_path(fb);
    }
    if (s_ready)
        HMI_LOG("audit: 落盘 %s（超过 %ldKB 滚成 .1）", s_path, AUDIT_MAX_BYTES / 1024);
    else
        HMI_LOG("audit: 没有可写目录，审计只走串口（HMI_AUDIT_DIR 可指定落点）");
}

const char* audit_path(void)
{
    return s_ready ? s_path : "";
}

static void rotate_if_needed(void)
{
    struct stat st;

    if (stat(s_path, &st) != 0 || st.st_size <= AUDIT_MAX_BYTES) return;
    (void)remove(s_path);            /* 只留一代：.1 先删，再 rename 当 .1 */
    {
        char old[232];

        snprintf(old, sizeof(old), "%s.1", s_path);
        (void)remove(old);
    }
}

static void write_line(const char* event, const char* user, const char* detail)
{
    char stamp[32];
    char who[40];
    time_t now = time(NULL);
    struct tm* tm_local = localtime(&now);

    if (tm_local) {
        strftime(stamp, sizeof(stamp), "%Y-%m-%dT%H:%M:%S", tm_local);
    } else {
        snprintf(stamp, sizeof(stamp), "epoch=%ld", (long)now);
    }
    snprintf(who, sizeof(who), "%s", (user && *user) ? user : "-");

    /* 先串口：落盘失败（只读卷、空间满）时这条记录不能凭空消失 */
    HMI_LOG("AUDIT %s %s user=%s groups=%s%s%s", stamp, event, who,
            auth_user_groups()[0] ? auth_user_groups() : "-",
            (detail && *detail) ? " " : "", detail ? detail : "");

    if (!s_ready) return;
    {
        FILE* f;

        rotate_if_needed();
        f = fopen(s_path, "a");
        if (!f) {
            /* 每次都报：配了审计却一直没日志，正是最该被发现的静默失败 */
            HMI_LOG("audit: 写不了 %s（%s），本条只留在串口", s_path, strerror(errno));
            return;
        }
        fprintf(f, "%s %s user=%s groups=%s%s%s\n", stamp, event, who,
                auth_user_groups()[0] ? auth_user_groups() : "-",
                (detail && *detail) ? " " : "", detail ? detail : "");
        (void)fclose(f);
    }
}

void audit_log(const char* event, const char* user, const char* detail_fmt, ...)
{
    char detail[192];

    if (!event || !*event) return;
    detail[0] = '\0';
    if (detail_fmt && *detail_fmt) {
        va_list ap;

        va_start(ap, detail_fmt);
        vsnprintf(detail, sizeof(detail), detail_fmt, ap);
        va_end(ap);
    }
    write_line(event, user ? user : auth_user_name(), detail);
}

void audit_denied(const char* what, const char* why)
{
    audit_log("denied", NULL, "%s%s%s", what ? what : "?",
              (why && *why) ? "：" : "", why ? why : "");
}

void audit_tag_write(const char* tag_name, const char* value_text)
{
    time_t now = time(NULL);

    if (s_tag_sec == now) {                 /* 同一秒里只落一条 */
        if (s_tag_dropped < 9999) s_tag_dropped++;
        return;
    }
    if (s_tag_dropped > 0) {
        audit_log("tag-write", NULL, "tag=%s value=%s（另有 %d 次写入已合并）",
                  tag_name ? tag_name : "?", value_text ? value_text : "?",
                  s_tag_dropped);
    } else {
        audit_log("tag-write", NULL, "tag=%s value=%s",
                  tag_name ? tag_name : "?", value_text ? value_text : "?");
    }
    s_tag_sec = now;
    s_tag_dropped = 0;
}
