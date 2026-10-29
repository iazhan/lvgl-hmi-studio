/*
 * tagwrite.c — 写变量：本地值 + 设备下发（见 tagwrite.h 的说明）
 */
#include "tagwrite.h"
#include "audit.h"
#include "core/tagdb.h"
#include "comm/modbus.h"
#include "comm/sim_cfg.h"
#include "hmi_log.h"

#include <stdio.h>
#include <string.h>          /* strlen：字符串写要按字节数截断到 src_len */

static const project_t* s_proj;
static sim_dev_t*       s_sim;

void tagwrite_init(const project_t* p, sim_dev_t* sim)
{
    s_proj = p;
    s_sim = sim;
}

static int push(int tag, double num, int is_bool)
{
    const tag_def_t* t;
    int rc;

    if (!s_proj || tag < 0 || tag >= s_proj->tags_count) return -1;
    t = &s_proj->tags[tag];
    /* 第 4 个参数是"布尔值"，不是"这是不是布尔写"：早先传 is_bool（恒为 1），
     * 于是布尔变量只会被写成 1 —— 开关打开后就再也关不掉。 */
    rc = t->src_dev_sim ? sim_write_tag(s_sim, t, num, is_bool ? (num != 0) : 0)
                        : modbus_write_tag(s_proj, tag, num, is_bool ? (num != 0) : 0);
    /* 让工程的 sim.lua 知道"上位机写了什么"（阀门、设定值这类联锁要靠它） */
    if (t->src_dev_sim) sim_cfg_on_write(tag, is_bool ? (double)(num != 0 ? 1 : 0) : num);
    return rc;
}

int tagwrite_num(int tag, double v)
{
    char buf[32];

    if (tagdb_write_num(tag, v) != 0) return -1;
    (void)push(tag, v, 0);
    /* 审计在本层拦一次就覆盖动作、写回、配方下载、脚本四条来源（ADR-0026） */
    snprintf(buf, sizeof(buf), "%.6g", v);
    if (s_proj && tag >= 0 && tag < s_proj->tags_count)
        audit_tag_write(s_proj->tags[tag].name, buf);
    return 0;
}

int tagwrite_bool(int tag, int b)
{
    if (tagdb_write_bool(tag, b) != 0) return -1;
    /* 值本身带上：sim.lua 的 on_write 看到的应该是 1/0，而不是设备的空值 */
    (void)push(tag, b ? 1.0 : 0.0, 1);
    if (s_proj && tag >= 0 && tag < s_proj->tags_count)
        audit_tag_write(s_proj->tags[tag].name, b ? "1" : "0");
    return 0;
}

int tagwrite_str(int tag, const char* s)
{
    const tag_def_t* t;
    int n;

    if (!s) return -1;
    if (!s_proj || tag < 0 || tag >= s_proj->tags_count) return -1;
    t = &s_proj->tags[tag];
    if (t->type != TAG_T_STRING) return -1;          /* 只有字符串变量能接字符串写 */
    if (!t->access_rw) return -1;                    /* 只读变量直接拒 */
    n = (int)strlen(s);
    if (t->src_len > 0 && n > t->src_len) n = t->src_len;
    tagdb_set_str(tag, s, n, TAG_Q_GOOD);
    if (t->src_dev_sim) (void)sim_write_str(s_sim, t, s, n);
    else (void)modbus_write_tag_str(s_proj, tag, s, n);
    {
        char buf[40];

        snprintf(buf, sizeof(buf), "\"%.*s\"", (int)(sizeof(buf) - 3), s);
        audit_tag_write(t->name, buf);
    }
    return 0;
}
