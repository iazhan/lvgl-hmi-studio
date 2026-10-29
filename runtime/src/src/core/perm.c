/*
 * perm.c — 权限词表与解析（见 perm.h 的说明）
 *
 * 这里只有纯解析与查表，没有会话状态；会话在 core/auth.c。
 */
#include "perm.h"
#include "core/auth.h"
#include "data/user_store.h"
#include "hmi_json.h"

#include <stdio.h>
#include <string.h>

/* ── 命名位 ── */

const char* perm_bit_name(unsigned bit)
{
#define PERM_NAME_(name, val) if (bit == (unsigned)(val)) return #name;
    HMI_PERM_LIST(PERM_NAME_)
#undef PERM_NAME_
    return NULL;
}

unsigned perm_bit_by_name(const char* name)
{
    if (!name || !*name) return 0;
#define PERM_LOOKUP_(nm, val) if (strcmp(name, #nm) == 0) return (unsigned)(val);
    HMI_PERM_LIST(PERM_LOOKUP_)
#undef PERM_LOOKUP_
    return 0;
}

int perm_parse_bits(const void* json, perm_bits_t* out, const char* who,
                    char* err, size_t errsz)
{
    const hmi_json_t* arr = (const hmi_json_t*)json;
    size_t n;

    if (!out) return -1;
    *out = 0;
    if (!arr) return 0;                              /* 没写 perms = 没有命名位 */
    if (hmi_json_type(arr) != HMI_JSON_ARR) {
        snprintf(err, errsz, "%s: perms 必须是字符串数组（取值见 spec/perms.json）", who);
        return -1;
    }
    n = hmi_json_count(arr);
    for (size_t i = 0; i < n; i++) {
        const char* name = hmi_json_str(hmi_json_at(arr, i), NULL);
        unsigned bit;

        if (!name || !*name) {
            snprintf(err, errsz, "%s: perms 里有空项", who);
            return -1;
        }
        bit = perm_bit_by_name(name);
        if (!bit) {
            snprintf(err, errsz, "%s: 未知权限 %s（取值见 spec/perms.json）", who, name);
            return -1;
        }
        *out |= (perm_bits_t)bit;
    }
    return 0;
}

void perm_bits_text(perm_bits_t m, char* out, size_t outsz)
{
    size_t used = 0;
    int first = 1;

    if (!out || outsz == 0) return;
    out[0] = '\0';
    if ((m & PERM_ALL) == 0) {
        snprintf(out, outsz, "(无)");
        return;
    }
#define PERM_TEXT_(name, val) \
    if (m & (perm_bits_t)(val)) { \
        int w = snprintf(out + used, outsz - used, "%s%s", first ? "" : ",", #name); \
        if (w > 0) { used += (size_t)w; if (used >= outsz) used = outsz - 1; } \
        first = 0; \
    }
    HMI_PERM_LIST(PERM_TEXT_)
#undef PERM_TEXT_
}

/* ── 组掩码（页面/控件的 perm）── */

static int perm_fill(const hmi_json_t* arr, perm_mask_t* out, const char* wid,
                     char* err, size_t errsz)
{
    size_t n;

    *out = 0;
    if (!arr) return 0;
    if (hmi_json_type(arr) != HMI_JSON_ARR) {
        snprintf(err, errsz, "控件 %s: perm 必须是数组或 {view,operate}", wid);
        return -1;
    }
    n = hmi_json_count(arr);
    if (n > USER_MAX_GROUPS) {
        snprintf(err, errsz, "控件 %s: perm 组数 %zu 超过 users.json 的组数上限 %d",
                 wid, n, USER_MAX_GROUPS);
        return -1;
    }
    for (size_t i = 0; i < n; i++) {
        const char* id = hmi_json_str(hmi_json_at(arr, i), NULL);
        int gi;

        if (!id || !*id) {
            snprintf(err, errsz, "控件 %s: perm 里有空组 id", wid);
            return -1;
        }
        gi = user_group_find(id);
        if (auth_enabled() && gi < 0) {
            snprintf(err, errsz, "控件 %s: perm 引用了 users.json 里不存在的组 %s", wid, id);
            return -1;
        }
        if (gi >= 0 && gi < 8) *out |= (perm_mask_t)(1u << gi);
    }
    return 0;
}

int perm_parse(const void* json, perm_mask_t* view, perm_mask_t* op,
               const char* wid, char* err, size_t errsz)
{
    const hmi_json_t* p = (const hmi_json_t*)json;

    if (!view || !op) return -1;
    *view = 0;
    *op = 0;
    if (!p) return 0;                              /* 没写 perm = 不限 */

    if (hmi_json_type(p) == HMI_JSON_ARR) {
        if (perm_fill(p, view, wid, err, errsz) != 0) return -1;
        *op = *view;                               /* 简写：能看也能操作 */
        return 0;
    }
    if (hmi_json_type(p) != HMI_JSON_OBJ) {
        snprintf(err, errsz, "控件 %s: perm 必须是数组或 {view,operate}", wid);
        return -1;
    }
    if (perm_fill(hmi_json_get(p, "view"), view, wid, err, errsz) != 0) return -1;
    if (hmi_json_get(p, "operate")) {
        if (perm_fill(hmi_json_get(p, "operate"), op, wid, err, errsz) != 0) return -1;
    } else {
        *op = *view;
    }
    /* operate 必须是 view 的子集：看不见却能操作，配置本身就是错的 */
    if ((*op & (perm_mask_t)~*view) != 0) {
        snprintf(err, errsz, "控件 %s: operate 里有组不在 view 里", wid);
        return -1;
    }
    return 0;
}
