/*
 * perm.h — 权限词表与解析（ADR-0012 / ADR-0026）
 *
 * 两层权限各管一件事，不要合并：
 *   组掩码 perm_mask_t：页面/控件的 perm 写组 id，**建页期**决定看得见/点得动；
 *   命名位 perm_bits_t：users.json 每组的 perms[]，**动作执行前**决定这个动作让不让做。
 *
 * 命名位的位号与编辑器 Permission 枚举（lvgl-hmi-studio
 * HmiEditor.Core/Models/User/Permission.cs）逐位对齐；词表真源是 spec/perms.json，
 * 改这里要同时跑 tools/check_perm.py（与 check_caps.py 同级）。
 */
#ifndef HMI_PERM_H
#define HMI_PERM_H

#include <stddef.h>
#include <stdint.h>

/* 组掩码：第 i 位 = users.json 里的第 i 个组（组数 ≤ 8）。
 * 0 = 不限。这样每个控件只多两个字节，150 个控件也不会把 widget_t 撑大。 */
typedef uint8_t perm_mask_t;

/* 命名权限位。位号与编辑器一致，顺序不要动（编辑器写进工程的掩码靠它对齐） */
enum {
    PERM_VIEW          = 1u << 0,   /* 查看画面：切页类动作 */
    PERM_OPERATE       = 1u << 1,   /* 操作控件：写变量/写设备/脚本 */
    PERM_EDIT_RECIPE   = 1u << 2,   /* 编辑配方：改值、下载、新建、导入 */
    PERM_ACK_ALARM     = 1u << 3,   /* 确认报警 */
    PERM_CHANGE_CONFIG = 1u << 4,   /* 修改系统配置：存盘组写盘 */
    PERM_MANAGE_USERS  = 1u << 5,   /* 管理用户：给别人改口令 */
    PERM_VIEW_HISTORY  = 1u << 6,   /* 查看历史：history_table / 报警历史 */
    PERM_EXPORT        = 1u << 7,   /* 导出数据：配方导出到 U 盘 */
    PERM_DEBUG         = 1u << 8,   /* 调试：直接写设备寄存器/线圈（绕过变量表） */
    PERM_SHELVE        = 1u << 9,   /* 搁置报警（与确认分开：静音是独立权力） */
    PERM_ALL           = (1u << 10) - 1
};

typedef uint16_t perm_bits_t;

/* 名字表：解析 users.json 的 perms[]、报错文案、check_perm.py 对表都用这一份 */
#define HMI_PERM_LIST(X) \
    X(view,            PERM_VIEW) \
    X(operate,         PERM_OPERATE) \
    X(edit_recipe,     PERM_EDIT_RECIPE) \
    X(ack_alarm,       PERM_ACK_ALARM) \
    X(change_config,   PERM_CHANGE_CONFIG) \
    X(manage_users,    PERM_MANAGE_USERS) \
    X(view_history,    PERM_VIEW_HISTORY) \
    X(export,          PERM_EXPORT) \
    X(debug,           PERM_DEBUG) \
    X(shelve,          PERM_SHELVE)

/* 单个位 → "ack_alarm"；不认识（含 0 与多位）返回 NULL */
const char* perm_bit_name(unsigned bit);

/* "ack_alarm" → 位；不认识返回 0 */
unsigned perm_bit_by_name(const char* name);

/* 解析 perms 数组（字符串名字）：写掩码。空数组 = 没有任何命名位（合法，但启动日志会点名）。
 * 未知名字报错并指到 spec/perms.json。0 成功，-1 失败。 */
int perm_parse_bits(const void* json, perm_bits_t* out, const char* who,
                    char* err, size_t errsz);

/* 掩码 → "view,operate"（给启动日志看）；m=0 时写 "(无)" */
void perm_bits_text(perm_bits_t m, char* out, size_t outsz);

/* 解析页面/控件的 perm：数组 = view 与 operate 相同；对象 = {view:[], operate:[]}。
 * 返回 0 成功，-1 失败（组不存在、operate 不在 view 里等）。 */
int perm_parse(const void* json, perm_mask_t* view, perm_mask_t* op,
               const char* wid, char* err, size_t errsz);

#endif /* HMI_PERM_H */
