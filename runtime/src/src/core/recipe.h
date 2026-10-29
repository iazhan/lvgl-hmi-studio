/*
 * recipe.h — 配方子系统（ADR-0009）
 *
 * 结构来自 <工程根>/recipes/<组名>.json（group / members / recipes[].items）；
 * 设备上改过的条目与「当前配方」存在 <data_dir>/recipes/rcp_<hash>.json，
 * 导入新工程时 members 完全相同才保留，否则备份成 .bak 并重置。
 *
 * 下载（配方 → 变量）与采集（变量 → 配方）都按 members 顺序逐项做，遇错即停。
 * v1 只支持数值与布尔成员：字符串成员没有写接口，加载期就报错。
 */
#ifndef HMI_RECIPE_H
#define HMI_RECIPE_H

#include <stddef.h>

#include "project/loader.h"

#define RCP_MAX_GROUPS   4
#define RCP_MAX_MEMBERS  32
#define RCP_MAX_ITEMS    50
#define RCP_NAME_MAX     24
#define RCP_STR_MAX      32      /* 字符串成员的单值上限（字节） */
#define RCP_DESC_MAX     48
#define RCP_GROUP_MAX    32

typedef struct {
    char name[RCP_NAME_MAX];
    char desc[RCP_DESC_MAX];
} recipe_meta_t;

typedef struct {
    char          group[RCP_GROUP_MAX];
    int           members[RCP_MAX_MEMBERS];       /* 成员变量 id，按列顺序 */
    int           nmembers;
    uint8_t       is_str[RCP_MAX_MEMBERS];        /* 该列是不是 string 变量 */
    int           has_str;                        /* 1 = 有字符串成员，svals 才有效 */
    char*         svals;                          /* 字符串成员的值：nitems*nmembers*RCP_STR_MAX */

    int           nitems;
    int           current;                        /* 当前配方下标，-1 = 无 */
    int           apply_on_start;                 /* 开机把当前配方下发到变量（工程默认文件里的开关） */
    recipe_meta_t meta[RCP_MAX_ITEMS];
    double        vals[RCP_MAX_ITEMS * RCP_MAX_MEMBERS];   /* 行主序：item * nmembers + mi */
} recipe_group_t;

/* 加载工程默认 + 合并设备副本；data_dir 是数据根（其下建 recipes/）。返回组数，失败 -1 */
int  recipe_load(const project_t* p, const char* data_dir, char* err, size_t errsz);

/* 开机下发：工程里声明了 apply_on_start 的组，把它的当前配方写进成员变量。
 * 要在 tagwrite_init 之后、建页之前调用；返回下发了的组数。 */
int  recipe_apply_on_start(void);
void recipe_free(void);

int  recipe_count(void);
const recipe_group_t* recipe_at(int gi);
int  recipe_find(const char* group);              /* 找不到返回 -1 */

/* 下载：把第 idx 条配方按 members 顺序写进变量；遇错停止并写 err。返回 0 成功 */
int  recipe_download(int gi, int idx, char* err, size_t errsz);

/* 下载 + 设为当前配方 + 落盘（界面上“点行选中”走这个） */
int  recipe_activate(int gi, int idx, char* err, size_t errsz);
/* 采集：把变量当前值存进第 idx 条配方（不落盘，调用方接着 recipe_save） */
int  recipe_capture(int gi, int idx);

/* 修改（都只改内存，调用方负责 recipe_save） */
int  recipe_add(int gi, const char* name, char* err, size_t errsz);
int  recipe_remove(int gi, int idx);
int  recipe_set_value(int gi, int idx, int mi, double v);

/* 字符串成员：写一格（值来自文本键盘）。mi 必须是 string 列，否则返回 -1 */
int  recipe_set_str(int gi, int idx, int mi, const char* s);
int  recipe_set_name(int gi, int idx, const char* name);
int  recipe_set_current(int gi, int idx);

/* 落盘：把第 gi 组的设备副本原子写一遍（临时文件 + rename） */
int  recipe_save(int gi, char* err, size_t errsz);

/* U 盘导入导出（ADR-0011）：dir 是 U 盘挂载点，文件在 <dir>/recipes/ 下。
 * 导出返回组数；导入返回导入的组数（0 = 没有可导入的），失败 -1。 */
int  recipe_export(const char* dir, char* err, size_t errsz);
int  recipe_import(const char* dir, char* err, size_t errsz);

#endif /* HMI_RECIPE_H */
