/*
 * recipe.c — 配方子系统（ADR-0009）
 *
 * 内存模型 + 操作；文件读写都在 recipe_store.c。下载/采集按 members 顺序逐项做，
 * 遇错即停（37 §6.9）；改一次写一次（调用方在改完后调 recipe_save）。
 */
#include "recipe.h"
#include "recipe_store.h"
#include "audit.h"
#include "tagdb.h"
#include "tagwrite.h"
#include "hmi_log.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static recipe_group_t s_groups[RCP_MAX_GROUPS];
static int            s_n;
static char           s_data_dir[192] = "/data/chenge";

static const project_t* s_proj;      /* 成员类型（string 列）要查工程 */

int recipe_load(const project_t* p, const char* data_dir, char* err, size_t errsz)
{
    s_proj = p;
    int n, used;

    recipe_free();
    snprintf(s_data_dir, sizeof(s_data_dir), "%s", data_dir ? data_dir : "/data/chenge");
    n = recipe_store_load_project(p, s_groups, RCP_MAX_GROUPS, err, errsz);
    if (n < 0) return -1;
    s_n = n;
    used = recipe_store_merge_device(p, s_data_dir, s_groups, n, err, errsz);
    if (used < 0) return -1;

    /* 成员必须是存在的、可写的数值/布尔变量（string 没有写接口） */
    for (int gi = 0; gi < s_n; gi++) {
        for (int mi = 0; mi < s_groups[gi].nmembers; mi++) {
            int tag = s_groups[gi].members[mi];
            const tag_t* t;

            if (tag < 0 || tag >= tagdb_count()) {
                snprintf(err, errsz, "配方 %s 的成员 %d 不是有效变量 id",
                         s_groups[gi].group, tag);
                return -1;
            }
            t = tagdb_at(tag);
            (void)t;                    /* string 成员现在也支持（走 tagwrite_str） */
        }
    }
    if (s_n > 0)
        HMI_LOG("recipe: 载入 %d 组（其中 %d 组用设备副本）", s_n, used);
    return s_n;
}

void recipe_free(void)
{
    for (int i = 0; i < s_n; i++) free(s_groups[i].svals);
    s_n = 0;
    memset(s_groups, 0, sizeof(s_groups));
}

int recipe_count(void)
{
    return s_n;
}

const recipe_group_t* recipe_at(int gi)
{
    if (gi < 0 || gi >= s_n) return NULL;
    return &s_groups[gi];
}

int recipe_find(const char* group)
{
    if (!group || !*group) return -1;
    for (int i = 0; i < s_n; i++)
        if (strcmp(s_groups[i].group, group) == 0) return i;
    return -1;
}

int recipe_download(int gi, int idx, char* err, size_t errsz)
{
    recipe_group_t* g;

    if (gi < 0 || gi >= s_n) return -1;
    g = &s_groups[gi];
    if (idx < 0 || idx >= g->nitems) return -1;
    for (int mi = 0; mi < g->nmembers; mi++) {
        int tag = g->members[mi];
        double v = g->vals[idx * g->nmembers + mi];
        const tag_t* t = tagdb_at(tag);

        /* 字符串成员：值存在 svals 里，走 tagwrite_str（本地值 + 设备下发） */
        if (g->is_str[mi]) {
            const char* s = g->svals + (((size_t)idx * (size_t)g->nmembers + (size_t)mi)
                                        * RCP_STR_MAX);

            if (tagwrite_str(tag, s) != 0) {
                snprintf(err, errsz, "配方 %s 下载到第 %d 个成员（变量 %d）失败，已停止",
                         g->meta[idx].name, mi + 1, tag);
                return -1;
            }
            continue;
        }
        /* 布尔成员必须走 write_bool：write_num 会被类型检查拒掉 */
        if (t && t->type == TAG_T_BOOL) {
            if (tagwrite_bool(tag, v != 0) != 0) {
                snprintf(err, errsz, "配方 %s 下载到第 %d 个成员（变量 %d）失败，已停止",
                         g->meta[idx].name, mi + 1, tag);
                return -1;
            }
        } else if (tagwrite_num(tag, v) != 0) {
            snprintf(err, errsz, "配方 %s 下载到第 %d 个成员（变量 %d）失败，已停止",
                     g->meta[idx].name, mi + 1, tag);
            return -1;
        }
    }
    return 0;
}

int recipe_capture(int gi, int idx)
{
    recipe_group_t* g;

    if (gi < 0 || gi >= s_n) return -1;
    g = &s_groups[gi];
    if (idx < 0 || idx >= g->nitems) return -1;
    for (int mi = 0; mi < g->nmembers; mi++) {
        if (g->is_str[mi]) {
            char buf[RCP_STR_MAX];
            int n = tagdb_str_copy(g->members[mi], buf, (int)sizeof(buf) - 1, NULL);

            if (n < 0) n = 0;
            buf[n] = '\0';
            snprintf(g->svals + (((size_t)idx * (size_t)g->nmembers + (size_t)mi) * RCP_STR_MAX),
                     RCP_STR_MAX, "%s", buf);
        } else {
            g->vals[idx * g->nmembers + mi] = tagdb_num(g->members[mi]);
        }
    }
    return 0;
}

int recipe_add(int gi, const char* name, char* err, size_t errsz)
{
    recipe_group_t* g;
    int idx;

    if (gi < 0 || gi >= s_n) return -1;
    g = &s_groups[gi];
    if (g->nitems >= RCP_MAX_ITEMS) {
        snprintf(err, errsz, "配方组 %s 已到上限 %d 条", g->group, RCP_MAX_ITEMS);
        return -1;
    }
    idx = g->nitems;
    if (name && *name) {
        snprintf(g->meta[idx].name, sizeof(g->meta[idx].name), "%s", name);
    } else {
        snprintf(g->meta[idx].name, sizeof(g->meta[idx].name), "R%d", idx + 1);
    }
    g->meta[idx].desc[0] = '\0';
    for (int mi = 0; mi < g->nmembers; mi++)
        g->vals[idx * g->nmembers + mi] = 0;
    g->nitems++;
    if (g->current < 0) g->current = idx;
    (void)recipe_capture(gi, idx);          /* 新条目以当前变量值为初值 */
    audit_log("recipe-new", NULL, "group=%s name=%s", g->group, g->meta[idx].name);
    return idx;
}

int recipe_remove(int gi, int idx)
{
    recipe_group_t* g;

    if (gi < 0 || gi >= s_n) return -1;
    g = &s_groups[gi];
    if (idx < 0 || idx >= g->nitems || g->nitems <= 1) return -1;
    for (int i = idx; i < g->nitems - 1; i++) {
        g->meta[i] = g->meta[i + 1];
        for (int mi = 0; mi < g->nmembers; mi++)
            g->vals[i * g->nmembers + mi] = g->vals[(i + 1) * g->nmembers + mi];
    }
    g->nitems--;
    if (g->current >= g->nitems) g->current = g->nitems - 1;
    if (g->current < 0) g->current = 0;
    return 0;
}

int recipe_set_str(int gi, int idx, int mi, const char* s)
{
    recipe_group_t* g;

    if (gi < 0 || gi >= s_n) return -1;
    g = &s_groups[gi];
    if (idx < 0 || idx >= g->nitems || mi < 0 || mi >= g->nmembers) return -1;
    if (!g->is_str[mi] || !g->svals || !s) return -1;
    snprintf(g->svals + (((size_t)idx * (size_t)g->nmembers + (size_t)mi) * RCP_STR_MAX),
             RCP_STR_MAX, "%s", s);
    return 0;
}

int recipe_set_value(int gi, int idx, int mi, double v)
{
    recipe_group_t* g;

    if (gi < 0 || gi >= s_n) return -1;
    g = &s_groups[gi];
    if (idx < 0 || idx >= g->nitems || mi < 0 || mi >= g->nmembers) return -1;
    g->vals[idx * g->nmembers + mi] = v;
    return 0;
}

int recipe_set_name(int gi, int idx, const char* name)
{
    recipe_group_t* g;

    if (gi < 0 || gi >= s_n || !name || !*name) return -1;
    g = &s_groups[gi];
    if (idx < 0 || idx >= g->nitems) return -1;
    snprintf(g->meta[idx].name, sizeof(g->meta[idx].name), "%s", name);
    return 0;
}

int recipe_set_current(int gi, int idx)
{
    recipe_group_t* g;

    if (gi < 0 || gi >= s_n) return -1;
    g = &s_groups[gi];
    if (idx < 0 || idx >= g->nitems) return -1;
    g->current = idx;
    return 0;
}

int recipe_apply_on_start(void)
{
    int applied = 0;

    for (int gi = 0; gi < s_n; gi++) {
        recipe_group_t* g = &s_groups[gi];
        char derr[128];

        if (!g->apply_on_start) continue;
        if (g->current < 0 || g->current >= g->nitems) continue;
        if (recipe_download(gi, g->current, derr, sizeof(derr)) != 0) {
            /* 失败不改 current：界面上那条仍是"上次选的配方"，但值没下去，日志点名 */
            HMI_LOG("recipe: 开机下发 %s/%s 失败：%s", g->group,
                    g->meta[g->current].name, derr);
            continue;
        }
        HMI_LOG("recipe: 开机下发当前配方 %s/%s", g->group, g->meta[g->current].name);
        applied++;
    }
    return applied;
}

int recipe_activate(int gi, int idx, char* err, size_t errsz)
{
    if (recipe_download(gi, idx, err, errsz) != 0) return -1;
    (void)recipe_set_current(gi, idx);
    if (gi >= 0 && gi < s_n && idx >= 0 && idx < s_groups[gi].nitems)
        audit_log("recipe-load", NULL, "group=%s name=%s", s_groups[gi].group,
                  s_groups[gi].meta[idx].name);
    return recipe_save(gi, err, errsz);
}

int recipe_export(const char* dir, char* err, size_t errsz)
{
    int ok = 0;

    for (int i = 0; i < s_n; i++) {
        if (recipe_store_export(dir, &s_groups[i], err, errsz) != 0)
            HMI_LOG("recipe: 导出 %s 失败：%s", s_groups[i].group, err);
        else
            ok++;
    }
    if (ok == 0 && s_n > 0 && err && errsz) snprintf(err, errsz, "导出失败");
    if (ok > 0) audit_log("recipe-export", NULL, "%d 组 → %s", ok, dir ? dir : "?");
    return ok;
}

int recipe_import(const char* dir, char* err, size_t errsz)
{
    int n = recipe_store_import_dir(s_proj, dir, s_groups, s_n, err, errsz);

    if (n <= 0) return n;
    /* 导入的组立刻写回设备副本，重启后还在 */
    for (int i = 0; i < s_n; i++) {
        char e2[128];

        if (recipe_save(i, e2, sizeof(e2)) != 0)
            HMI_LOG("recipe: 导入后保存 %s 失败：%s", s_groups[i].group, e2);
    }
    HMI_LOG("recipe: 从 U 盘导入 %d 组", n);
    audit_log("recipe-import", NULL, "%d 组 ← %s", n, dir ? dir : "?");
    return n;
}

int recipe_save(int gi, char* err, size_t errsz)
{
    if (gi < 0 || gi >= s_n) return -1;
    return recipe_store_save_group(s_data_dir, &s_groups[gi], err, errsz);
}
