/*
 * recipe_store.c — 配方的文件层（ADR-0009）
 *
 * 读：工程默认（一组一文件）+ 设备副本（members 一致才采用，否则备份 .bak 重置）
 * 写：临时文件 + rename，掉电不留半截文件
 */
#include "recipe_store.h"
#include "hmi_json.h"
#include "hmi_log.h"

#include <dirent.h>
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

static uint32_t group_hash(const char* s)
{
    uint32_t h = 2166136261u;

    while (*s) {
        h ^= (uint32_t)(unsigned char)*s++;
        h *= 16777619u;
    }
    return h;
}

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

static void device_path(char* out, size_t outsz, const char* data_dir, const char* group)
{
    snprintf(out, outsz, "%s/recipes/rcp_%08x.json", data_dir, group_hash(group));
}

/* ── 解析一个组（工程默认与设备副本共用） ── */

static int parse_group(const project_t* p, const hmi_json_t* root, recipe_group_t* g,
                       const char* fallback_name, int need_current, char* err, size_t errsz)
{
    const hmi_json_t* members = hmi_json_get(root, "members");
    const hmi_json_t* recipes = hmi_json_get(root, "recipes");
    const char* name = hmi_json_str(hmi_json_get(root, "group"), fallback_name);
    size_t nm, ni;

    memset(g, 0, sizeof(*g));
    g->current = -1;
    snprintf(g->group, sizeof(g->group), "%s", (name && *name) ? name : "配方");

    if (hmi_json_type(members) != HMI_JSON_ARR) {
        snprintf(err, errsz, "配方组 %s 缺 members 数组", g->group);
        return -1;
    }
    nm = hmi_json_count(members);
    if (nm < 1 || nm > RCP_MAX_MEMBERS) {
        snprintf(err, errsz, "配方组 %s 的 members 项数 %zu 越界（1..%d）",
                 g->group, nm, RCP_MAX_MEMBERS);
        return -1;
    }
    for (size_t i = 0; i < nm; i++) {
        long id = hmi_json_int(hmi_json_at(members, i), -1);

        if (id < 0) {
            snprintf(err, errsz, "配方组 %s 的 members[%zu] 不是变量 id", g->group, i);
            return -1;
        }
        g->members[i] = (int)id;
    }
    g->nmembers = (int)nm;
    {
        int has_str = 0;

        for (size_t i = 0; i < nm; i++) {
            const tag_def_t* td = project_tag(p, g->members[i]);

            g->is_str[i] = (td && td->type == TAG_T_STRING) ? 1 : 0;
            if (g->is_str[i]) has_str = 1;
        }
        g->has_str = has_str;
    }

    if (hmi_json_type(recipes) != HMI_JSON_ARR) {
        snprintf(err, errsz, "配方组 %s 缺 recipes 数组", g->group);
        return -1;
    }
    ni = hmi_json_count(recipes);
    if (g->has_str) {
        /* 只有含字符串成员的组才分配这块（items × members × 32 字节），其余组为 NULL */
        g->svals = calloc(ni * nm * RCP_STR_MAX, 1);
        if (!g->svals) {
            snprintf(err, errsz, "配方组 %s 分配字符串成员缓冲失败", g->group);
            return -1;
        }
    }
    if (ni > RCP_MAX_ITEMS) {
        snprintf(err, errsz, "配方组 %s 的 recipes 项数 %zu 超过上限 %d",
                 g->group, ni, RCP_MAX_ITEMS);
        return -1;
    }
    for (size_t i = 0; i < ni; i++) {
        const hmi_json_t* it = hmi_json_at(recipes, i);
        const char* n = hmi_json_str(hmi_json_get(it, "name"), NULL);
        const char* d = hmi_json_str(hmi_json_get(it, "desc"), "");
        const hmi_json_t* items = hmi_json_get(it, "items");

        if (!n || !*n) {
            snprintf(err, errsz, "配方组 %s 的第 %zu 条缺 name", g->group, i);
            return -1;
        }
        snprintf(g->meta[i].name, sizeof(g->meta[i].name), "%s", n);
        snprintf(g->meta[i].desc, sizeof(g->meta[i].desc), "%s", d ? d : "");
        /* 缺项按 0 补（规范说"按变量初值"，v1 用 0 并在日志里点名） */
        if (hmi_json_type(items) != HMI_JSON_ARR || hmi_json_count(items) != nm) {
            HMI_LOG("配方 %s/%s 的 items 项数与 members(%zu) 不一致，缺项按 0 补",
                    g->group, g->meta[i].name, nm);
        }
        for (size_t k = 0; k < nm; k++) {
            int have = (hmi_json_type(items) == HMI_JSON_ARR && k < hmi_json_count(items));

            if (g->is_str[k]) {
                const char* s = have ? hmi_json_str(hmi_json_at(items, k), "") : "";

                snprintf(g->svals + ((i * nm + k) * RCP_STR_MAX), RCP_STR_MAX, "%s",
                         s ? s : "");
            } else {
                g->vals[i * nm + k] = have ? hmi_json_num(hmi_json_at(items, k), 0) : 0;
            }
        }
    }
    g->nitems = (int)ni;

    /* 工程默认没有 current 字段：默认选第一条，否则保存时会写出空 current */
    if (!need_current) {
        g->current = g->nitems > 0 ? 0 : -1;
        /* 开机是否下发由工程决定，设备副本不携带这个开关 */
        g->apply_on_start = hmi_json_bool(hmi_json_get(root, "apply_on_start"), 0);
    }
    if (need_current) {
        const char* cur = hmi_json_str(hmi_json_get(root, "current"), NULL);

        g->current = 0;
        if (cur && *cur) {
            int found = -1;

            for (int i = 0; i < g->nitems; i++)
                if (strcmp(g->meta[i].name, cur) == 0) { found = i; break; }
            if (found >= 0) g->current = found;
        }
        if (g->nitems == 0) g->current = -1;
    }
    return 0;
}

static char* read_file(const char* path, size_t* len)
{
    FILE* f = fopen(path, "rb");
    char* buf;
    long n;

    if (!f) return NULL;
    if (fseek(f, 0, SEEK_END) != 0 || (n = ftell(f)) < 0 || n > 512 * 1024) {
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

int recipe_store_load_project(const project_t* p, recipe_group_t* groups, int max,
                              char* err, size_t errsz)
{
    char dir[320];
    DIR* d;
    struct dirent* ent;
    int n = 0;

    if (!p) return 0;
    snprintf(dir, sizeof(dir), "%s/recipes", p->root);
    d = opendir(dir);
    if (!d) return 0;                       /* 没有配方目录是正常情况 */

    while ((ent = readdir(d)) != NULL) {
        char path[512];
        char* text;
        hmi_json_t* root;
        size_t len = 0;
        size_t nl = strlen(ent->d_name);
        char base[RCP_GROUP_MAX];

        if (nl < 6 || strcmp(ent->d_name + nl - 5, ".json") != 0) continue;
        if (n >= max) {
            snprintf(err, errsz, "配方组超过上限 %d（%s 未加载）", max, ent->d_name);
            closedir(d);
            return -1;
        }
        if (snprintf(path, sizeof(path), "%s/%s", dir, ent->d_name) >= (int)sizeof(path)) {
            snprintf(err, errsz, "配方路径过长（%s）", ent->d_name);
            closedir(d);
            return -1;
        }
        text = read_file(path, &len);
        if (!text) {
            snprintf(err, errsz, "读不了 %s", path);
            closedir(d);
            return -1;
        }
        root = hmi_json_parse(text, len, err, errsz);
        if (!root) {
            free(text);
            closedir(d);
            return -1;
        }
        snprintf(base, sizeof(base), "%.*s", (int)(nl - 5), ent->d_name);
        if (parse_group(p, root, &groups[n], base, 0, err, errsz) != 0) {
            hmi_json_free(root);
            free(text);
            closedir(d);
            return -1;
        }
        hmi_json_free(root);
        free(text);
        n++;
    }
    closedir(d);
    return n;
}

int recipe_store_merge_device(const project_t* p, const char* data_dir,
                              recipe_group_t* groups, int n,
                              char* err, size_t errsz)
{
    int used = 0;

    for (int i = 0; i < n; i++) {
        char path[320];
        char* text;
        hmi_json_t* root;
        recipe_group_t dev;
        size_t len = 0;

        device_path(path, sizeof(path), data_dir, groups[i].group);
        text = read_file(path, &len);
        if (!text) continue;                /* 没有设备副本：用工程默认 */
        root = hmi_json_parse(text, len, err, errsz);
        if (!root) {
            HMI_LOG("配方 %s 的设备副本解析失败，改用工程默认：%s", groups[i].group, err);
            free(text);
            continue;
        }
        if (parse_group(p, root, &dev, groups[i].group, 1, err, errsz) != 0) {
            HMI_LOG("配方 %s 的设备副本内容不合法，改用工程默认：%s", groups[i].group, err);
            hmi_json_free(root);
            free(text);
            continue;
        }
        hmi_json_free(root);
        free(text);

        /* 结构比对：members 完全一致才采用（34 §15.13） */
        if (dev.nmembers == groups[i].nmembers &&
            memcmp(dev.members, groups[i].members, sizeof(int) * (size_t)dev.nmembers) == 0) {
            int keep = groups[i].apply_on_start;   /* 开关来自工程，副本里没有 */

            free(groups[i].svals);                 /* 工程默认那份字符串缓冲换掉 */
            groups[i] = dev;
            groups[i].apply_on_start = keep;
            used++;
        } else {
            char bak[352];

            snprintf(bak, sizeof(bak), "%s.bak", path);
            if (rename(path, bak) == 0)
                HMI_LOG("配方 %s 的 members 变了，旧副本备份为 %s", groups[i].group, bak);
            else
                HMI_LOG("配方 %s 的 members 变了，旧副本备份失败（%s）",
                        groups[i].group, strerror(errno));
        }
    }
    return used;
}

/* JSON 字符串转义：名字只收 ASCII，但仍要处理引号与反斜杠 */
static void json_escape(const char* in, char* out, size_t outsz)
{
    size_t o = 0;

    for (const char* p = in; *p && o + 2 < outsz; p++) {
        if (*p == '"' || *p == '\\') out[o++] = '\\';
        out[o++] = *p;
    }
    out[o] = '\0';
}

int recipe_store_export(const char* dir, const recipe_group_t* g, char* err, size_t errsz)
{
    char path[320];
    char sub[288];

    if (!dir || !*dir || !g) return -1;
    snprintf(sub, sizeof(sub), "%s/recipes", dir);
    mkdir_p(sub);
    /* 文件名用组名哈希：FAT32 上中文名容易乱码，组名放文件内容里 */
    snprintf(path, sizeof(path), "%s/rcp_%08x.json", sub, group_hash(g->group));
    return recipe_store_write_json(path, g, err, errsz);
}

int recipe_store_import_dir(const project_t* p, const char* dir,
                            recipe_group_t* groups, int n,
                            char* err, size_t errsz)
{
    char sub[288];
    DIR* d;
    struct dirent* ent;
    int used = 0;

    if (!dir || !*dir || !groups) return -1;
    snprintf(sub, sizeof(sub), "%s/recipes", dir);
    d = opendir(sub);
    if (!d) {
        snprintf(err, errsz, "U 盘里没有 recipes 目录（%s）", sub);
        return -1;
    }
    while ((ent = readdir(d)) != NULL) {
        char path[512];
        size_t nl = strlen(ent->d_name);
        recipe_group_t dev;
        int gi = -1;

        if (nl < 6 || strcmp(ent->d_name + nl - 5, ".json") != 0) continue;
        if (snprintf(path, sizeof(path), "%s/%s", sub, ent->d_name) >= (int)sizeof(path)) {
            HMI_LOG("recipe: 跳过 %s（路径过长）", ent->d_name);
            continue;
        }
        if (recipe_store_read_json(p, path, &dev, err, errsz) != 0) {
            HMI_LOG("recipe: 跳过 %s（%s）", ent->d_name, err);
            continue;
        }
        for (int i = 0; i < n; i++)
            if (strcmp(groups[i].group, dev.group) == 0) { gi = i; break; }
        if (gi < 0) {
            HMI_LOG("recipe: 跳过 %s（工程里没有组 %s）", ent->d_name, dev.group);
            continue;
        }
        if (dev.nmembers != groups[gi].nmembers ||
            memcmp(dev.members, groups[gi].members,
                   sizeof(int) * (size_t)dev.nmembers) != 0) {
            HMI_LOG("recipe: 跳过 %s（组 %s 的 members 与工程不一致）", ent->d_name, dev.group);
            continue;
        }
        groups[gi] = dev;
        used++;
    }
    closedir(d);
    if (used == 0 && err && errsz) snprintf(err, errsz, "没有可导入的配方文件");
    return used;
}

/* 把一组写成 JSON 文件（临时文件 + rename，掉电不留半截）。
 * 设备副本与 U 盘导出共用这一份写法，避免两处格式漂移。 */
int recipe_store_write_json(const char* path, const recipe_group_t* g,
                            char* err, size_t errsz)
{
    char tmp[352];
    FILE* f;
    char esc[RCP_NAME_MAX * 2 + 2];

    if (!g || !path || !*path) return -1;
    snprintf(tmp, sizeof(tmp), "%s.tmp", path);

    f = fopen(tmp, "wb");
    if (!f) {
        snprintf(err, errsz, "写不了 %s（%s）", tmp, strerror(errno));
        return -1;
    }
    fprintf(f, "{\n  \"group\": \"");
    json_escape(g->group, esc, sizeof(esc));
    fprintf(f, "%s\",\n  \"current\": \"", esc);
    json_escape(g->current >= 0 ? g->meta[g->current].name : "", esc, sizeof(esc));
    fprintf(f, "%s\",\n  \"members\": [", esc);
    for (int i = 0; i < g->nmembers; i++)
        fprintf(f, "%s%d", i ? "," : "", g->members[i]);
    fprintf(f, "],\n  \"recipes\": [");
    for (int i = 0; i < g->nitems; i++) {
        fprintf(f, "%s\n    {\"name\": \"", i ? "," : "");
        json_escape(g->meta[i].name, esc, sizeof(esc));
        fprintf(f, "%s\", \"desc\": \"", esc);
        json_escape(g->meta[i].desc, esc, sizeof(esc));
        fprintf(f, "%s", esc);              /* desc 也要写出去（之前转义了却没打印） */
        fprintf(f, "\", \"items\": [");
        for (int k = 0; k < g->nmembers; k++) {
            if (g->is_str[k]) {
                json_escape(g->svals + ((i * g->nmembers + k) * RCP_STR_MAX), esc,
                            sizeof(esc));
                fprintf(f, "%s\"%s\"", k ? ", " : "", esc);
            } else {
                fprintf(f, "%s%.6g", k ? ", " : "", g->vals[i * g->nmembers + k]);
            }
        }
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

int recipe_store_save_group(const char* data_dir, const recipe_group_t* g,
                            char* err, size_t errsz)
{
    char path[320];
    char dir[256];

    if (!g) return -1;
    snprintf(dir, sizeof(dir), "%s/recipes", data_dir);
    mkdir_p(dir);
    device_path(path, sizeof(path), data_dir, g->group);
    return recipe_store_write_json(path, g, err, errsz);
}

/* 读一个配方 JSON 文件（导入用）：current 也要读回来 */
int recipe_store_read_json(const project_t* p, const char* path, recipe_group_t* g,
                           char* err, size_t errsz)
{
    char* text;
    hmi_json_t* root;
    size_t len = 0;
    int rc;

    text = read_file(path, &len);
    if (!text) {
        snprintf(err, errsz, "读不了 %s", path);
        return -1;
    }
    root = hmi_json_parse(text, len, err, errsz);
    if (!root) {
        free(text);
        return -1;
    }
    rc = parse_group(p, root, g, "配方", 1, err, errsz);
    hmi_json_free(root);
    free(text);
    return rc;
}
