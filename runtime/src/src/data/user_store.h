/*
 * user_store.h — 用户表（ADR-0012）
 *
 * 工程默认 <工程根>/users.json（37 §6.9）；运行时读写 <data>/state/users.json。
 * 设备副本总是保留：导入新工程不改设备上的用户（34 §15.13）。
 * 口令格式 `sha256$<16位hex盐>$<64位hex摘要>`，空串 = 免密。
 */
#ifndef HMI_USER_STORE_H
#define HMI_USER_STORE_H

#include <stddef.h>

#include "project/loader.h"

#define USER_MAX_GROUPS 8
#define USER_MAX_USERS  16
#define USER_NAME_MAX   24
#define USER_GID_MAX    24
#define USER_PASS_MAX   96

typedef struct {
    char id[USER_GID_MAX];
    char name[32];
    int  admin;
    /* 命名权限位（ADR-0026）：perms[] 的掩码。has_perms 区分"没写"与"写了空数组"——
     * 前者是组没配，后者是明确不给任何位；两者的日志与默认语义不能混。 */
    unsigned perms;
    int      has_perms;
} user_group_t;

typedef struct {
    char name[USER_NAME_MAX];
    char pass[USER_PASS_MAX];                       /* 空 = 免密 */
    char groups[USER_MAX_GROUPS][USER_GID_MAX];
    int  ngroups;
} user_t;

/* 加载：工程默认 + 设备副本（设备副本存在就整份采用）。返回用户数，失败 -1 */
int  user_store_load(const project_t* p, const char* data_dir, char* err, size_t errsz);

int  user_group_count(void);
const user_group_t* user_group_at(int i);
int  user_group_find(const char* id);              /* 找不到 -1 */

int  user_count(void);
const user_t* user_at(int i);
int  user_find(const char* name);                  /* 找不到 -1 */

/* 校验口令：0 通过、-1 失败（免密用户任何口令都通过） */
int  user_verify(int ui, const char* pwd);

/* 把当前表写回设备副本（原子写） */
int  user_store_save(const char* data_dir, char* err, size_t errsz);

/* 口令最短长度（manifest.password_min_len，4..16）。改密与界面提示都读它，
 * 免得"提示写 4 位、代码要求 6 位"。 */
void user_store_password_policy(int min_len);
int  user_password_min_len(void);

/* 给某个用户设新口令：生成新盐、算摘要、立刻写设备副本。
 * 口令长度 = user_password_min_len()..16、只收 ASCII 可见字符（调用方已校验也可，
 * 这里再兜一层）。成功后写一条 audit 的 pwd-change。 */
int  user_set_password(int ui, const char* pwd, char* err, size_t errsz);

#endif /* HMI_USER_STORE_H */
