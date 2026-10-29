/*
 * recipe_store.h — 配方的文件层（ADR-0009 决定 2/4）
 *
 * 两处数据：
 *   工程默认 <工程根>/recipes/<组名>.json         （只读，一组一文件）
 *   设备副本 <data_dir>/recipes/rcp_<hash>.json（可写，含 current，整份副本）
 * 设备副本的 members 与工程一致就用它，否则把旧副本改名成 .bak 再重置。
 * 写入走「临时文件 + rename」，掉电不会留半截文件。
 */
#ifndef HMI_RECIPE_STORE_H
#define HMI_RECIPE_STORE_H

#include <stddef.h>

#include "core/recipe.h"

/* 读 <root>/recipes/<组名>.json，填进 groups（最多 max 组）。返回组数，失败 -1 */
int  recipe_store_load_project(const project_t* p, recipe_group_t* groups, int max,
                               char* err, size_t errsz);

/* 合并设备副本（members 一致才采用）；返回采用了几组，失败 -1 */
int  recipe_store_merge_device(const project_t* p, const char* data_dir,
                              recipe_group_t* groups, int n,
                               char* err, size_t errsz);

/* 原子写一组 */
int  recipe_store_save_group(const char* data_dir, const recipe_group_t* g,
                             char* err, size_t errsz);

/* 原子写到一个指定路径（设备副本与 U 盘导出共用同一份 JSON 写法） */
int  recipe_store_write_json(const char* path, const recipe_group_t* g,
                             char* err, size_t errsz);

/* 读一个配方 JSON 文件到 g（含 current） */
int  recipe_store_read_json(const project_t* p, const char* path, recipe_group_t* g,
                            char* err, size_t errsz);

/* 导出到 U 盘目录：<dir>/recipes/rcp_<组名哈希>.json（文件名 ASCII 安全，
 * 组名在文件内容的 group 字段里）。成功返回 0。 */
int  recipe_store_export(const char* dir, const recipe_group_t* g, char* err, size_t errsz);

/* 从 U 盘目录导入：扫 <dir>/recipes 下的 json，按 group 字段匹配，
 * members 一致才覆盖；返回导入的组数，失败 -1。 */
int  recipe_store_import_dir(const project_t* p, const char* dir,
                             recipe_group_t* groups, int n,
                             char* err, size_t errsz);

#endif /* HMI_RECIPE_STORE_H */
