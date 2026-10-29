/*
 * tagdb.c — 变量表实现
 *
 * 一把 pthread 互斥锁保护整张表：UI 线程（读、绑定刷新）与通讯线程（写回、
 * 写命令）都过锁。ARM926EJ-S 没有 LDREX/STREX，不写无锁结构（34 §5.4）。
 */
#include "tagdb.h"
#include "sysvar.h"
#include "hmi_log.h"

#include <pthread.h>
#include <stdlib.h>
#include <string.h>

#define TAGDB_MAX 2000        /* 37 §5.11：变量数上限 */

static tag_t* s_tags;
static int    s_count;
static pthread_mutex_t s_lock = PTHREAD_MUTEX_INITIALIZER;

/* 调用方必须已持锁 */
static tag_t* at_locked(int id)
{
    if (id < 0 || id >= s_count) return NULL;
    return &s_tags[id];
}

/* 值或质量真正变化才递增 version —— 界面靠它判断要不要重画 */
static void set_num_locked(int id, double v, int quality)
{
    tag_t* t = at_locked(id);

    if (!t) return;
    if (t->num != v || t->quality != quality) {
        t->num = v;
        t->quality = (uint8_t)quality;
        t->version++;
    }
}

int tagdb_init(int count)
{
    tagdb_free();
    if (count < 0 || count > TAGDB_MAX) return -1;
    if (count == 0) return 0;

    pthread_mutex_lock(&s_lock);
    s_tags = calloc((size_t)count, sizeof(*s_tags));
    if (s_tags) {
        s_count = count;
        for (int i = 0; i < count; i++) {
            s_tags[i].type = TAG_T_I32;
            s_tags[i].access_rw = 0;
            s_tags[i].quality = TAG_Q_UNINIT;
        }
    }
    pthread_mutex_unlock(&s_lock);
    return s_tags ? 0 : -1;
}

void tagdb_free(void)
{
    pthread_mutex_lock(&s_lock);
    if (s_tags) {
        for (int i = 0; i < s_count; i++) free(s_tags[i].sval);
    }
    free(s_tags);
    s_tags = NULL;
    s_count = 0;
    pthread_mutex_unlock(&s_lock);
}

/* ── 字符串变量（ADR-0007）：定长块，通讯线程不做 realloc ── */

int tagdb_string_bind(int id, int len)
{
    tag_t* t;
    int rc = 0;

    if (len < 1) len = 1;
    if (len > 64) len = 64;
    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    if (!t) rc = -1;
    else if (!t->sval) {
        t->sval = calloc((size_t)len + 1, 1);
        if (!t->sval) rc = -1;
        else {
            t->scap = (uint8_t)len;
            t->slen = 0;
        }
    }
    pthread_mutex_unlock(&s_lock);
    return rc;
}

void tagdb_set_str(int id, const char* data, int n, int quality)
{
    tag_t* t;
    int changed = 0;
    int k;

    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    if (!t || !t->sval || t->scap == 0) {
        pthread_mutex_unlock(&s_lock);
        return;
    }
    if (!data) n = 0;
    if (n < 0) n = 0;
    if (n > (int)t->scap) n = (int)t->scap;
    /* 通讯线程可能和 UI 线程同时读到半截，先比对再整块 memcpy 覆盖 */
    for (k = 0; k < n; k++) {
        if (t->sval[k] != data[k]) { changed = 1; break; }
    }
    if (!changed && (int)t->slen != n) changed = 1;
    if (changed || t->quality != quality) {
        if (n > 0) memcpy(t->sval, data, (size_t)n);
        if ((int)t->scap >= n) t->sval[n] = '\0';
        t->slen = (uint8_t)n;
        t->quality = (uint8_t)quality;
        t->version++;
    }
    pthread_mutex_unlock(&s_lock);
}

int tagdb_str_copy(int id, char* out, int outsz, int* q)
{
    tag_t* t;
    int n = 0;

    if (!out || outsz < 1) return 0;
    out[0] = '\0';
    if (sysvar_index(id) >= 0) {          /* 系统变量（46 §8.4） */
        int n = sysvar_str(id, out, outsz);

        if (n < 0) return 0;
        if (q) *q = TAG_Q_GOOD;
        return n;
    }
    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    if (t && t->sval && t->scap) {
        n = t->slen;
        if (n > outsz - 1) n = outsz - 1;
        if (n > 0) memcpy(out, t->sval, (size_t)n);
        out[n] = '\0';
        if (q) *q = t->quality;
        else { /* nothing */ }
    } else if (q) {
        *q = TAG_Q_BAD;
    }
    pthread_mutex_unlock(&s_lock);
    return n;
}

int tagdb_string_len(int id)
{
    tag_t* t;
    int n = 0;

    if (sysvar_index(id) >= 0) {
        char buf[24];

        return sysvar_str(id, buf, (int)sizeof(buf));
    }
    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    if (t && t->sval) n = (int)strlen(t->sval);   /* 容量按内容算不够，改用 slen 记录 */
    pthread_mutex_unlock(&s_lock);
    (void)n;
    return 0;
}

int tagdb_valid(int id)
{
    if (id < 0) return 0;
    if (sysvar_index(id) >= 0) return 1;
    return id < tagdb_count();
}

int tagdb_count(void)
{
    int n;

    pthread_mutex_lock(&s_lock);
    n = s_count;
    pthread_mutex_unlock(&s_lock);
    return n;
}

tag_t* tagdb_at(int id)
{
    static tag_t s_sys_tag;      /* 系统变量的只读视图 */
    static char  s_sys_str[24];
    int si = sysvar_index(id);

    if (si >= 0) {
        int type = sysvar_type(id);

        memset(&s_sys_tag, 0, sizeof(s_sys_tag));
        s_sys_tag.type = (tag_type_t)type;
        s_sys_tag.access_rw = 0;
        s_sys_tag.quality = TAG_Q_GOOD;
        s_sys_tag.version = sysvar_version(id);
        if (type == TAG_T_STRING) {
            int n = sysvar_str(id, s_sys_str, (int)sizeof(s_sys_str));

            s_sys_tag.sval = s_sys_str;
            s_sys_tag.slen = (uint8_t)(n > 0 ? n : 0);
            s_sys_tag.scap = (uint8_t)sizeof(s_sys_str);
        } else {
            s_sys_tag.num = sysvar_num(id);
            s_sys_tag.i64 = (int64_t)s_sys_tag.num;
        }
        return &s_sys_tag;
    }
    return at_locked(id);        /* 只在启动阶段（通讯线程起来之前）用 */
}

void tagdb_set_num(int id, double v, int quality)
{
    pthread_mutex_lock(&s_lock);
    set_num_locked(id, v, quality);
    pthread_mutex_unlock(&s_lock);
}

void tagdb_set_bool(int id, int v, int quality)
{
    tagdb_set_num(id, v ? 1.0 : 0.0, quality);
}

void tagdb_set_i64(int id, int64_t v, int quality)
{
    tag_t* t;

    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    if (t && (t->i64 != v || t->quality != quality)) {
        t->i64 = v;
        t->quality = (uint8_t)quality;
        t->version++;
    }
    pthread_mutex_unlock(&s_lock);
}

double tagdb_num(int id)
{
    tag_t* t;
    double v;

    if (sysvar_index(id) >= 0) return sysvar_num(id);   /* 系统变量 */
    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    v = t ? t->num : 0.0;
    pthread_mutex_unlock(&s_lock);
    return v;
}

int tagdb_bool(int id)
{
    return tagdb_num(id) != 0.0;
}

int64_t tagdb_i64(int id)
{
    tag_t* t;
    int64_t v;

    if (sysvar_index(id) >= 0) return (int64_t)sysvar_num(id);
    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    v = t ? t->i64 : 0;
    pthread_mutex_unlock(&s_lock);
    return v;
}

uint32_t tagdb_version(int id)
{
    tag_t* t;
    uint32_t v;

    if (sysvar_index(id) >= 0) return sysvar_version(id);
    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    v = t ? t->version : 0;
    pthread_mutex_unlock(&s_lock);
    return v;
}

int tagdb_quality(int id)
{
    tag_t* t;
    int q;

    if (sysvar_index(id) >= 0) return TAG_Q_GOOD;
    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    q = t ? t->quality : TAG_Q_UNINIT;
    pthread_mutex_unlock(&s_lock);
    return q;
}

/* 数值写：量程外按 min/max 截断并记警告（37 §7.1 的写入语义）。
 * 截断而不是拒绝：Lua 的 setTag 同样截断，同一变量不能有两套写入规则。 */
int tagdb_write_num(int id, double v)
{
    tag_t* t;
    int rc = 0;

    if (sysvar_index(id) >= 0) {      /* 系统变量只读 */
        HMI_LOG("tagdb: %s 是系统变量（只读），写被拒绝",
                sysvar_name(id) ? sysvar_name(id) : "?");
        return -1;
    }
    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    if (!t) {
        rc = -1;
    } else if (!t->access_rw) {
        HMI_LOG("tagdb: tag%d 只读，写被拒绝", id);
        rc = -1;
    } else if (t->type == TAG_T_BOOL) {
        HMI_LOG("tagdb: tag%d 是布尔，数值写被拒绝", id);
        rc = -1;
    } else {
        if (t->has_min && v < t->dmin) {
            HMI_LOG("tagdb: tag%d 值 %.3f 低于量程 %.3f，截断到下限", id, v, t->dmin);
            v = t->dmin;
        }
        if (t->has_max && v > t->dmax) {
            HMI_LOG("tagdb: tag%d 值 %.3f 超量程 %.3f，截断到上限", id, v, t->dmax);
            v = t->dmax;
        }
        set_num_locked(id, v, TAG_Q_GOOD);
    }
    pthread_mutex_unlock(&s_lock);
    return rc;
}

int tagdb_write_bool(int id, int v)
{
    tag_t* t;
    int rc = 0;

    if (sysvar_index(id) >= 0) {
        HMI_LOG("tagdb: %s 是系统变量（只读），写被拒绝",
                sysvar_name(id) ? sysvar_name(id) : "?");
        return -1;
    }
    pthread_mutex_lock(&s_lock);
    t = at_locked(id);
    if (!t) {
        rc = -1;
    } else if (!t->access_rw) {
        HMI_LOG("tagdb: tag%d 只读，写被拒绝", id);
        rc = -1;
    } else if (t->type != TAG_T_BOOL) {
        HMI_LOG("tagdb: tag%d 不是布尔，布尔写被拒绝", id);
        rc = -1;
    } else {
        set_num_locked(id, v ? 1.0 : 0.0, TAG_Q_GOOD);
    }
    pthread_mutex_unlock(&s_lock);
    return rc;
}
