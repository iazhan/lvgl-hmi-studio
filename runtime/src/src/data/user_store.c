/*
 * user_store.c — 用户表（ADR-0012）
 *
 * 工程默认 + 设备副本；口令是 sha256$salt$hash（空 = 免密）。
 * 校验失败/成功都只在本层判断，会话与锁定在 core/auth.c。
 */
#include "user_store.h"
#include "sha256.h"
#include "core/audit.h"
#include "core/auth.h"
#include "core/perm.h"
#include "hmi_json.h"
#include "hmi_log.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

static user_group_t s_groups[USER_MAX_GROUPS];
static int          s_ngroups;
static user_t       s_users[USER_MAX_USERS];
static int          s_nusers;
static char         s_dir[192] = "/data/chenge";
static int          s_pwd_min = 4;           /* manifest.password_min_len，4..16 */

static void mkdir_p(const char* path)
{
    char tmp[256];
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

static char* read_file(const char* path, size_t* len)
{
    FILE* f = fopen(path, "rb");
    char* buf;
    long n;

    if (!f) return NULL;
    if (fseek(f, 0, SEEK_END) != 0 || (n = ftell(f)) < 0 || n > 256 * 1024) {
        fclose(f);
        return NULL;
    }
    fseek(f, 0, SEEK_SET);
    buf = malloc((size_t)n + 1);
    if (!buf) {
        fclose(f);
        return NULL;
    }
    if (fread(buf, 1, (size_t)n, f) != (size_t)n) {
        free(buf);
        fclose(f);
        return NULL;
    }
    buf[n] = '\0';
    fclose(f);
    if (len) *len = (size_t)n;
    return buf;
}

/* ── 解析 ── */

static int parse_table(const hmi_json_t* root, char* err, size_t errsz)
{
    const hmi_json_t* groups = hmi_json_get(root, "groups");
    const hmi_json_t* users = hmi_json_get(root, "users");
    size_t ng, nu;

    s_ngroups = 0;
    s_nusers = 0;

    if (hmi_json_type(groups) == HMI_JSON_ARR) {
        ng = hmi_json_count(groups);
        if (ng > USER_MAX_GROUPS) {
            snprintf(err, errsz, "users.json 的 groups 项数 %zu 超过上限 %d",
                     ng, USER_MAX_GROUPS);
            return -1;
        }
        for (size_t i = 0; i < ng; i++) {
            const hmi_json_t* g = hmi_json_at(groups, i);
            const char* id = hmi_json_str(hmi_json_get(g, "id"), NULL);
            const char* name = hmi_json_str(hmi_json_get(g, "name"), NULL);

            if (!id || !*id) {
                snprintf(err, errsz, "users.json 第 %zu 个组缺 id", i);
                return -1;
            }
            if (strlen(id) >= USER_GID_MAX) {
                snprintf(err, errsz, "users.json 组 id %s 过长（≤%d）", id, USER_GID_MAX - 1);
                return -1;
            }
            for (int k = 0; k < s_ngroups; k++)
                if (strcmp(s_groups[k].id, id) == 0) {
                    snprintf(err, errsz, "users.json 组 id %s 重复", id);
                    return -1;
                }
            snprintf(s_groups[s_ngroups].id, USER_GID_MAX, "%s", id);
            snprintf(s_groups[s_ngroups].name, sizeof(s_groups[s_ngroups].name), "%s",
                     (name && *name) ? name : id);
            s_groups[s_ngroups].admin = hmi_json_bool(hmi_json_get(g, "admin"), 0);
            /* 命名权限位（ADR-0026）：没写 = 组不参与命名位判定；写了空数组 = 明确不给 */
            s_groups[s_ngroups].perms = 0;
            s_groups[s_ngroups].has_perms = (hmi_json_get(g, "perms") != NULL);
            if (s_groups[s_ngroups].has_perms) {
                char who[80];
                perm_bits_t bits = 0;

                snprintf(who, sizeof(who), "users.json 组 %s", id);
                if (perm_parse_bits(hmi_json_get(g, "perms"), &bits, who, err, errsz) != 0)
                    return -1;
                s_groups[s_ngroups].perms = bits;
            }
            s_ngroups++;
        }
    }

    if (hmi_json_type(users) != HMI_JSON_ARR) {
        snprintf(err, errsz, "users.json 缺 users 数组");
        return -1;
    }
    nu = hmi_json_count(users);
    if (nu < 1) {
        snprintf(err, errsz, "users.json 里没有用户");
        return -1;
    }
    if (nu > USER_MAX_USERS) {
        snprintf(err, errsz, "users.json 的 users 项数 %zu 超过上限 %d", nu, USER_MAX_USERS);
        return -1;
    }
    for (size_t i = 0; i < nu; i++) {
        const hmi_json_t* u = hmi_json_at(users, i);
        const char* name = hmi_json_str(hmi_json_get(u, "name"), NULL);
        const char* pass = hmi_json_str(hmi_json_get(u, "password"), "");
        const hmi_json_t* gs = hmi_json_get(u, "groups");
        user_t* d = &s_users[s_nusers];

        if (!name || !*name) {
            snprintf(err, errsz, "users.json 第 %zu 个用户缺 name", i);
            return -1;
        }
        if (strlen(name) >= USER_NAME_MAX) {
            snprintf(err, errsz, "users.json 用户名 %s 过长（≤%d）", name, USER_NAME_MAX - 1);
            return -1;
        }
        for (int k = 0; k < s_nusers; k++)
            if (strcmp(s_users[k].name, name) == 0) {
                snprintf(err, errsz, "users.json 用户名 %s 重复", name);
                return -1;
            }
        memset(d, 0, sizeof(*d));
        snprintf(d->name, sizeof(d->name), "%s", name);
        snprintf(d->pass, sizeof(d->pass), "%s", pass ? pass : "");

        /* 口令：空 = 免密；否则必须是 sha256$salt$hash */
        if (d->pass[0] && strncmp(d->pass, "sha256$", 7) != 0) {
            snprintf(err, errsz, "用户 %s 的 password 不是 sha256$salt$hash 格式"
                                 "（明文口令不允许）", name);
            return -1;
        }
        if (hmi_json_type(gs) != HMI_JSON_ARR || hmi_json_count(gs) < 1) {
            snprintf(err, errsz, "用户 %s 至少要属于一个组", name);
            return -1;
        }
        for (size_t k = 0; k < hmi_json_count(gs); k++) {
            const char* gid = hmi_json_str(hmi_json_at(gs, k), NULL);

            if (!gid || user_group_find(gid) < 0) {
                snprintf(err, errsz, "用户 %s 引用了不存在的组 %s", name, gid ? gid : "(空)");
                return -1;
            }
            if (d->ngroups < USER_MAX_GROUPS)
                snprintf(d->groups[d->ngroups++], USER_GID_MAX, "%s", gid);
        }
        s_nusers++;
    }

    /* 必须至少有一个管理员用户，否则现场会把自己锁在外面 */
    {
        int has_admin = 0;

        for (int i = 0; i < s_nusers && !has_admin; i++)
            for (int k = 0; k < s_users[i].ngroups; k++) {
                int gi = user_group_find(s_users[i].groups[k]);

                if (gi >= 0 && s_groups[gi].admin) { has_admin = 1; break; }
            }
        if (!has_admin) {
            snprintf(err, errsz, "users.json 里没有属于管理员组的用户（会把现场锁死）");
            return -1;
        }
    }
    return s_nusers;
}

int user_store_load(const project_t* p, const char* data_dir, char* err, size_t errsz)
{
    char path[320];
    char* text;
    hmi_json_t* root;
    size_t len = 0;

    s_ngroups = s_nusers = 0;
    if (data_dir && *data_dir) snprintf(s_dir, sizeof(s_dir), "%s", data_dir);
    if (!p) return 0;

    /* 设备副本优先：导入新工程时保留设备上的用户（34 §15.13） */
    snprintf(path, sizeof(path), "%s/state/users.json", s_dir);
    text = read_file(path, &len);
    if (!text) {
        snprintf(path, sizeof(path), "%s/users.json", p->root);
        text = read_file(path, &len);
        if (!text) return 0;                    /* 没有用户表 = 不启用权限 */
    }
    root = hmi_json_parse(text, len, err, errsz);
    if (!root) {
        free(text);
        return -1;
    }
    if (parse_table(root, err, errsz) < 0) {
        hmi_json_free(root);
        free(text);
        return -1;
    }
    hmi_json_free(root);
    free(text);
    HMI_LOG("user: 载入 %d 组 %d 个用户", s_ngroups, s_nusers);
    return s_nusers;
}

int user_group_count(void)
{
    return s_ngroups;
}

const user_group_t* user_group_at(int i)
{
    if (i < 0 || i >= s_ngroups) return NULL;
    return &s_groups[i];
}

int user_group_find(const char* id)
{
    if (!id || !*id) return -1;
    for (int i = 0; i < s_ngroups; i++)
        if (strcmp(s_groups[i].id, id) == 0) return i;
    return -1;
}

int user_count(void)
{
    return s_nusers;
}

const user_t* user_at(int i)
{
    if (i < 0 || i >= s_nusers) return NULL;
    return &s_users[i];
}

int user_find(const char* name)
{
    if (!name || !*name) return -1;
    for (int i = 0; i < s_nusers; i++)
        if (strcmp(s_users[i].name, name) == 0) return i;
    return -1;
}

int user_verify(int ui, const char* pwd)
{
    const user_t* u = user_at(ui);
    char salt[17];
    char want[65];
    const char* p;

    if (!u) return -1;
    if (u->pass[0] == '\0') return 0;           /* 免密 */
    if (!pwd) return -1;
    if (strncmp(u->pass, "sha256$", 7) != 0) return -1;

    p = u->pass + 7;
    if (strlen(p) < 16 + 1 + 64) return -1;
    memcpy(salt, p, 16);
    salt[16] = '\0';
    sha256_password(salt, pwd, 1000, want);
    return strcmp(want, p + 17) == 0 ? 0 : -1;
}

/* 盐：优先 /dev/urandom，取不到就用时间与 pid 凑——目的是让同名口令摘要不同，
 * 不是密码学级随机源（口令本身已经加盐多轮哈希） */
static void make_salt(char out[17])
{
    unsigned char b[8] = { 0 };
    FILE* f = fopen("/dev/urandom", "rb");

    if (f) {
        if (fread(b, 1, sizeof(b), f) != sizeof(b)) memset(b, 0, sizeof(b));
        fclose(f);
    }
    if (b[0] == 0 && b[1] == 0) {
        unsigned t = (unsigned)time(NULL) ^ (unsigned)(getpid() << 8);

        memcpy(b, &t, sizeof(t) < sizeof(b) ? sizeof(t) : sizeof(b));
    }
    for (int i = 0; i < 8; i++) snprintf(out + i * 2, 3, "%02x", b[i]);
    out[16] = '\0';
}

void user_store_password_policy(int min_len)
{
    s_pwd_min = (min_len < 4) ? 4 : (min_len > 16) ? 16 : min_len;
    HMI_LOG("user: 口令最短 %d 位", s_pwd_min);
}

int user_password_min_len(void)
{
    return s_pwd_min;
}

int user_set_password(int ui, const char* pwd, char* err, size_t errsz)
{
    user_t* u;

    if (ui < 0 || ui >= s_nusers || !pwd) return -1;
    if ((int)strlen(pwd) < s_pwd_min || strlen(pwd) > 16) {
        snprintf(err, errsz, "口令长度需 %d~16 位", s_pwd_min);
        return -1;
    }
    u = &s_users[ui];
    {
        char salt[17];
        char hex[65];

        make_salt(salt);
        sha256_password(salt, pwd, 1000, hex);
        snprintf(u->pass, sizeof(u->pass), "sha256$%s$%s", salt, hex);
    }
    if (user_store_save(s_dir, err, errsz) != 0) {
        u->pass[0] = '\0';                   /* 落盘失败就回退，别让内存与盘上不一致 */
        return -1;
    }
    HMI_LOG("user: %s 的口令已更新", u->name);
    audit_log("pwd-change", u->name, "操作者=%s", auth_user_name() ? auth_user_name() : "未登录");
    return 0;
}

int user_store_save(const char* data_dir, char* err, size_t errsz)
{
    char dir[256];
    char path[320];
    char tmp[352];
    FILE* f;

    snprintf(dir, sizeof(dir), "%s/state", data_dir ? data_dir : s_dir);
    mkdir_p(dir);
    snprintf(path, sizeof(path), "%s/users.json", dir);
    snprintf(tmp, sizeof(tmp), "%s.tmp", path);

    f = fopen(tmp, "wb");
    if (!f) {
        snprintf(err, errsz, "写不了 %s（%s）", tmp, strerror(errno));
        return -1;
    }
    fprintf(f, "{\n  \"groups\": [");
    for (int i = 0; i < s_ngroups; i++) {
        fprintf(f, "%s\n    {\"id\": \"%s\", \"name\": \"%s\", \"admin\": %s",
                i ? "," : "", s_groups[i].id, s_groups[i].name,
                s_groups[i].admin ? "true" : "false");
        /* 命名位必须回写：设备副本优先于工程默认值，丢了 perms 等于改次口令就把
         * 整个权限表降级成"只有组掩码" */
        if (s_groups[i].has_perms) {
            int first = 1;

            fprintf(f, ", \"perms\": [");
#define USER_WRITE_PERM_(name, val) \
            if (s_groups[i].perms & (unsigned)(val)) { \
                fprintf(f, "%s\"%s\"", first ? "" : ", ", #name); \
                first = 0; \
            }
            HMI_PERM_LIST(USER_WRITE_PERM_)
#undef USER_WRITE_PERM_
            fprintf(f, "]");
        }
        fprintf(f, "}");
    }
    fprintf(f, "\n  ],\n  \"users\": [");
    for (int i = 0; i < s_nusers; i++) {
        fprintf(f, "%s\n    {\"name\": \"%s\", \"password\": \"%s\", \"groups\": [",
                i ? "," : "", s_users[i].name, s_users[i].pass);
        for (int k = 0; k < s_users[i].ngroups; k++)
            fprintf(f, "%s\"%s\"", k ? ", " : "", s_users[i].groups[k]);
        fprintf(f, "]}");
    }
    fprintf(f, "\n  ]\n}\n");
    if (fflush(f) != 0 || fsync(fileno(f)) != 0) {
        snprintf(err, errsz, "写 %s 失败（%s）", tmp, strerror(errno));
        fclose(f);
        unlink(tmp);
        return -1;
    }
    fclose(f);
    if (rename(tmp, path) != 0) {
        snprintf(err, errsz, "rename %s 失败（%s）", path, strerror(errno));
        unlink(tmp);
        return -1;
    }
    return 0;
}
