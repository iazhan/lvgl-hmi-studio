/*
 * vfs.c —— 内存文件表（工程本体读文件时的第一命中点）
 *
 * 表很小（一个工程几十个文件），用线性数组足够；不排序、不做哈希，省得
 * 为了几十条记录引入一个哈希表还要解释桶大小。
 */
#include "core/vfs.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define VFS_MAX_FILES 256

typedef struct {
    char*  path;      /* 相对工程根 */
    void*  data;
    size_t len;
} vfs_entry_t;

static vfs_entry_t s_files[VFS_MAX_FILES];
static int         s_count;
static char        s_root[256];

void vfs_clear(void)
{
    for (int i = 0; i < s_count; i++) {
        free(s_files[i].path);
        free(s_files[i].data);
    }
    memset(s_files, 0, sizeof(s_files));
    s_count = 0;
    s_root[0] = '\0';
}

void vfs_set_root(const char* root)
{
    if (!root || !*root) {
        s_root[0] = '\0';
        return;
    }
    snprintf(s_root, sizeof(s_root), "%s", root);
    {
        size_t n = strlen(s_root);

        while (n > 1 && s_root[n - 1] == '/') s_root[--n] = '\0';
    }
}

int vfs_put(const char* path, const void* data, size_t len)
{
    char* copy;

    if (!path || !*path || !data || len == 0) return -1;
    if (s_count >= VFS_MAX_FILES) return -1;

    /* 同名覆盖：编辑器热更新同一页时会再放一次 */
    for (int i = 0; i < s_count; i++) {
        if (strcmp(s_files[i].path, path) == 0) {
            copy = (char*)malloc(len);
            if (!copy) return -1;
            memcpy(copy, data, len);
            free(s_files[i].data);
            s_files[i].data = copy;
            s_files[i].len = len;
            return 0;
        }
    }

    s_files[s_count].path = strdup(path);
    s_files[s_count].data = malloc(len);
    if (!s_files[s_count].path || !s_files[s_count].data) {
        free(s_files[s_count].path);
        free(s_files[s_count].data);
        return -1;
    }
    memcpy(s_files[s_count].data, data, len);
    s_files[s_count].len = len;
    s_count++;
    return 0;
}

const void* vfs_get(const char* abs_or_rel, size_t* len)
{
    const char* key = abs_or_rel;
    size_t rl;

    if (s_count == 0 || !abs_or_rel) return NULL;   /* 进程版走这条，零开销 */

    /* 绝对路径 → 剥掉工程根，变成表里的相对键 */
    rl = strlen(s_root);
    if (rl > 0 && strncmp(abs_or_rel, s_root, rl) == 0 && abs_or_rel[rl] == '/')
        key = abs_or_rel + rl + 1;

    for (int i = 0; i < s_count; i++) {
        if (strcmp(s_files[i].path, key) == 0) {
            if (len) *len = s_files[i].len;
            return s_files[i].data;
        }
    }
    return NULL;
}

int vfs_count(void)
{
    return s_count;
}
