/*
 * sim.c — 模拟设备实现
 *
 * 寄存器布局（37 §5 source）：addr 为 0 基；i16/u16 占 1 个寄存器，
 * i32/u32/f32 占 2 个，i64/double 占 4 个；字序与字节序取 ABCD 大端
 * （sim 设备无 default_order 字段，按 37 §4 缺省）。
 */
#include "sim.h"
#include "core/tagdb.h"
#include "hmi_log.h"

#include <string.h>

/* 设备侧不再自带生成规则：取值由工程的 sim.json / sim.lua 决定（ADR-0015），
 * 本文件只负责寄存器模型与编解码（执行器）。没有配置文件时寄存器保持 0。 */

static int reg_count(int datatype)
{
    switch (datatype) {
    case DT_BOOL:
    case DT_I16:
    case DT_U16:  return 1;
    case DT_I32:
    case DT_U32:
    case DT_F32:  return 2;
    case DT_I64:
    case DT_DOUBLE: return 4;
    default:      return -1;   /* DT_STRING 等 v1 不支持 */
    }
}

/* discrete 与 coil 共用位数组、input 与 holding 共用寄存器数组（37 §5
 * 「sim 使用同一套内存模型」）；discrete/input 是只读视图。 */
static int area_is_bit(int area)
{
    return area == AREA_COIL || area == AREA_DISCRETE;
}

static int area_ok(int area)
{
    return area_is_bit(area) || area == AREA_INPUT || area == AREA_HOLDING;
}

static int addr_ok(int area, int addr, int datatype)
{
    int n;

    if (!area_ok(area)) return 0;
    n = reg_count(datatype);
    if (n < 0) return 0;
    if (area_is_bit(area)) return addr >= 0 && addr < SIM_COIL_MAX;
    return addr >= 0 && addr + n <= SIM_HOLDING_MAX;
}

static double decode_num(const sim_dev_t* s, int area, int addr, int datatype);
static void encode_num(sim_dev_t* s, int area, int addr, int datatype, double v);

void sim_init(sim_dev_t* s)
{
    memset(s, 0, sizeof(*s));
}

void sim_tick(sim_dev_t* s, uint32_t elapsed_ms)
{
    (void)s;
    (void)elapsed_ms;        /* 取值由 sim_cfg 的两层驱动，这里只保留接口 */
}

/* ── 编解码：ABCD 大端 ── */

static double decode_num(const sim_dev_t* s, int area, int addr, int datatype)
{
    if (area_is_bit(area)) {
        int byte = addr / 8, bit = addr % 8;
        return (s->coil[byte] >> bit) & 1 ? 1.0 : 0.0;
    }
    switch (datatype) {
    case DT_I16: return (int16_t)s->holding[addr];
    case DT_U16: return (double)s->holding[addr];
    case DT_I32: {
        uint32_t u = ((uint32_t)s->holding[addr] << 16) | s->holding[addr + 1];
        return (double)(int32_t)u;
    }
    case DT_U32: {
        uint32_t u = ((uint32_t)s->holding[addr] << 16) | s->holding[addr + 1];
        return (double)u;
    }
    case DT_F32: {
        uint32_t u = ((uint32_t)s->holding[addr] << 16) | s->holding[addr + 1];
        float f;
        memcpy(&f, &u, sizeof(f));
        return (double)f;
    }
    case DT_I64:
    case DT_DOUBLE: {
        uint64_t u = 0;
        for (int i = 0; i < 4; i++)
            u = (u << 16) | s->holding[addr + i];
        if (datatype == DT_I64) return (double)(int64_t)u;
        {
            double d;
            memcpy(&d, &u, sizeof(d));
            return d;
        }
    }
    default:
        return 0.0;
    }
}

static void encode_num(sim_dev_t* s, int area, int addr, int datatype, double v)
{
    if (area_is_bit(area)) {
        int byte = addr / 8, bit = addr % 8;
        if (v != 0.0) s->coil[byte] |= (uint8_t)(1u << bit);
        else s->coil[byte] &= (uint8_t)~(1u << bit);
        return;
    }
    switch (datatype) {
    case DT_I16: s->holding[addr] = (uint16_t)(int16_t)v; break;
    case DT_U16: s->holding[addr] = (uint16_t)v; break;
    case DT_I32: {
        uint32_t u = (uint32_t)(int32_t)v;
        s->holding[addr] = (uint16_t)(u >> 16);
        s->holding[addr + 1] = (uint16_t)(u & 0xffff);
        break;
    }
    case DT_U32: {
        uint32_t u = (uint32_t)v;
        s->holding[addr] = (uint16_t)(u >> 16);
        s->holding[addr + 1] = (uint16_t)(u & 0xffff);
        break;
    }
    case DT_F32: {
        float f = (float)v;
        uint32_t u;
        memcpy(&u, &f, sizeof(u));
        s->holding[addr] = (uint16_t)(u >> 16);
        s->holding[addr + 1] = (uint16_t)(u & 0xffff);
        break;
    }
    case DT_I64:
    case DT_DOUBLE: {
        uint64_t u;
        if (datatype == DT_I64) u = (uint64_t)(int64_t)v;
        else memcpy(&u, &v, sizeof(u));
        for (int i = 0; i < 4; i++)
            s->holding[addr + i] = (uint16_t)(u >> (48 - 16 * i));
        break;
    }
    default:
        break;
    }
}

int sim_read(const sim_dev_t* s, int area, int addr, int datatype,
             double* num, int* boolean)
{
    if (!area_ok(area) || !addr_ok(area, addr, datatype)) return -1;
    if (boolean) {
        *boolean = 0;
        if (area_is_bit(area) || datatype == DT_BOOL)
            *boolean = decode_num(s, area, addr, datatype) != 0.0;
    }
    if (num) *num = decode_num(s, area, addr, datatype);
    return 0;
}

int sim_write(sim_dev_t* s, int area, int addr, int datatype,
              double num, int boolean)
{
    /* discrete/input 是只读视图（37 §5），写请求一律拒绝 */
    if (area == AREA_DISCRETE || area == AREA_INPUT) {
        HMI_LOG("sim: area %d 只读，写被拒绝", area);
        return -1;
    }
    if (!addr_ok(area, addr, datatype)) {
        HMI_LOG("sim: 地址越界 area=%d addr=%d", area, addr);
        return -1;
    }
    encode_num(s, area, addr, datatype,
               (area_is_bit(area) || datatype == DT_BOOL) ? (boolean ? 1.0 : 0.0) : num);
    return 0;
}

/* ── Tag DB 同步 ── */

void sim_sync_tags(const sim_dev_t* s, const tag_def_t* tags, int count)
{
    for (int i = 0; i < count; i++) {
        const tag_def_t* t = &tags[i];
        if (!t->has_source || !t->src_dev_sim) continue;

        if (t->src_datatype == DT_STRING) {
            /* 字符串按寄存器铺（每寄存器 2 字节，ABCD 大端）：常量由 sim.json 的 consts 写入，
             * 这里读回成字节串，遇 0 结束。 */
            char buf[64];
            int cap = (int)sizeof(buf) - 1;
            int n = 0;

            if (t->src_len > 0 && t->src_len < cap) cap = t->src_len;
            for (int k = 0; k < cap; k++) {
                int reg = t->src_addr + k / 2;
                int hi, lo;

                if (reg < 0 || reg >= SIM_HOLDING_MAX) break;
                hi = (k & 1) ? (s->holding[reg] & 0xFF) : (s->holding[reg] >> 8);
                if (hi == 0) break;
                buf[n++] = (char)hi;
                if ((k & 1) == 0 && k + 1 >= cap) break;
                (void)lo;
            }
            buf[n] = '\0';
            tagdb_set_str(i, buf, n, TAG_Q_GOOD);
            continue;
        }
        if (t->src_datatype == DT_BOOL) {
            int b = 0;
            if (sim_read(s, t->src_area, t->src_addr, t->src_datatype, NULL, &b) == 0)
                tagdb_set_bool(i, b, TAG_Q_GOOD);
        } else if (t->type == TAG_T_I64 || t->type == TAG_T_DOUBLE) {
            double v = 0.0;
            if (sim_read(s, t->src_area, t->src_addr, t->src_datatype, &v, NULL) == 0)
                tagdb_set_i64(i, (int64_t)v, TAG_Q_GOOD);
        } else {
            double v = 0.0;
            if (sim_read(s, t->src_area, t->src_addr, t->src_datatype, &v, NULL) == 0)
                tagdb_set_num(i, v, TAG_Q_GOOD);
        }
    }
}

int sim_write_tag(sim_dev_t* s, const tag_def_t* t, double num, int boolean)
{
    if (!t->has_source || !t->src_dev_sim) return 0;   /* 内部变量：不进设备 */
    if (sim_write(s, t->src_area, t->src_addr, t->src_datatype, num, boolean) != 0)
        return -1;
    return 0;
}

int sim_write_str(sim_dev_t* s, const tag_def_t* t, const char* bytes, int n)
{
    int cap;

    if (!s || !t || !bytes) return -1;
    if (!t->has_source || !t->src_dev_sim) return 0;
    if (t->src_area != AREA_HOLDING) return -1;
    cap = (t->src_len > 0) ? t->src_len : n;
    if (n > cap) n = cap;
    for (int k = 0; k < cap; k++) {                    /* 先清干净，旧的尾巴别留着 */
        int reg = t->src_addr + k / 2;

        if (reg < 0 || reg >= SIM_HOLDING_MAX) break;
        if (k & 1) s->holding[reg] &= 0xFF00;
        else s->holding[reg] &= 0x00FF;
    }
    for (int k = 0; k < n; k++) {                      /* 偶数位放高字节，与读回一致 */
        int reg = t->src_addr + k / 2;
        uint8_t b = (uint8_t)bytes[k];

        if (reg < 0 || reg >= SIM_HOLDING_MAX) break;
        if (k & 1) s->holding[reg] = (uint16_t)((s->holding[reg] & 0xFF00) | b);
        else s->holding[reg] = (uint16_t)((s->holding[reg] & 0x00FF) | ((uint16_t)b << 8));
    }
    return 0;
}
