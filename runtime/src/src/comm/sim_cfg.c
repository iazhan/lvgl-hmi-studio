/*
 * sim_cfg.c — 工程的模拟设备（ADR-0015）
 *
 * sim.json 先跑（声明式生成器 + 只读常量），sim.lua 后跑（逻辑，以它为准）。
 * 脚本在独立的 lua_State 里，只给设备侧 API，不给 cg.*；单拍有指令预算，
 * 出错就停用脚本，界面照常跑——模拟是预览用的假设备，不该有能力拦住启动。
 */
#include "sim_cfg.h"
#include "core/tagdb.h"
#include "hmi_json.h"
#include "hmi_log.h"
#include "data/gbk_table.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "lua.h"
#include "lauxlib.h"
#include "lualib.h"

#define SIMCFG_MAX_GENS   16
#define SIMCFG_MAX_CONSTS 16
#define SIMCFG_BUDGET     200000      /* 单拍指令数上限（约 1~2ms，F1C100s 上足够跑几十行） */

typedef struct {
    int      area, addr, datatype;
    double   init, step, min, max;
    int      wrap;                    /* 0 reset、1 zero、2 hold */
    uint32_t period_ms, acc_ms;
} sim_gen_t;

static const project_t* s_proj;
static sim_dev_t*       s_sim;

static sim_gen_t s_gens[SIMCFG_MAX_GENS];
static int       s_ngens;
static uint32_t  s_gen_tick = 1000;   /* 生成器缺省周期（工程写 tick_ms 覆盖） */

static lua_State* s_L;
static int        s_lua_on_tick;      /* 有 on_tick 才每拍调用 */
static int        s_lua_on_write;
static int        s_disabled;         /* 运行期出错后停用 */
static uint32_t   s_tick_ms = 100;    /* 两层共用的节拍 */
static uint32_t   s_acc_ms;
static int        s_abort;

static char s_err[256];
static int  s_have_err;

/* ── 名字映射 ── */

static int area_from_name(const char* s)
{
    if (!s) return -1;
    if (strcmp(s, "coil") == 0) return AREA_COIL;
    if (strcmp(s, "discrete") == 0) return AREA_DISCRETE;
    if (strcmp(s, "input") == 0) return AREA_INPUT;
    if (strcmp(s, "holding") == 0) return AREA_HOLDING;
    return -1;
}

static int dt_from_name(const char* s)
{
    if (!s) return -1;
    if (strcmp(s, "bool") == 0) return DT_BOOL;
    if (strcmp(s, "i16") == 0) return DT_I16;
    if (strcmp(s, "u16") == 0) return DT_U16;
    if (strcmp(s, "i32") == 0) return DT_I32;
    if (strcmp(s, "u32") == 0) return DT_U32;
    if (strcmp(s, "f32") == 0) return DT_F32;
    if (strcmp(s, "i64") == 0) return DT_I64;
    if (strcmp(s, "double") == 0) return DT_DOUBLE;
    if (strcmp(s, "string") == 0) return DT_STRING;
    return -1;
}

static const char* area_name(int area)
{
    switch (area) {
    case AREA_COIL:     return "coil";
    case AREA_DISCRETE: return "discrete";
    case AREA_INPUT:    return "input";
    default:            return "holding";
    }
}


static void set_err(const char* fmt, ...)
{
    va_list ap;

    va_start(ap, fmt);
    vsnprintf(s_err, sizeof(s_err), fmt, ap);
    va_end(ap);
    s_have_err = 1;
    HMI_LOG("sim: %s", s_err);
}

const char* sim_cfg_take_error(void)
{
    static char buf[256];

    if (!s_have_err) return NULL;
    snprintf(buf, sizeof(buf), "%s", s_err);
    s_have_err = 0;
    return buf;
}

int sim_cfg_active(void)
{
    return s_ngens > 0 || (s_L && !s_disabled);
}

/* ── sim.json ── */

static char* read_text(const char* path, size_t* len)
{
    FILE* f = fopen(path, "rb");
    long n;
    char* buf;

    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    n = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (n <= 0 || n > 256 * 1024) {
        fclose(f);
        return NULL;
    }
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
    if (len) *len = (size_t)n;
    fclose(f);
    return buf;
}

/* 工程文件是 UTF-8，设备侧字符串按 GBK 存：先查表转一遍，界面才解得回来。
 * gbk_uni_map 是 GBK→Unicode，反查就线性扫（只在加载期做，几十个字符，代价可忽略）。 */
static int utf8_to_gbk(const char* in, char* out, int cap)
{
    int n = 0;
    const unsigned char* p = (const unsigned char*)in;

    while (*p && n < cap - 1) {
        unsigned cp;

        if (*p < 0x80) {
            out[n++] = (char)*p++;
            continue;
        }
        if ((*p & 0xE0) == 0xC0 && p[1]) {
            cp = ((unsigned)(p[0] & 0x1F) << 6) | (p[1] & 0x3F);
            p += 2;
        } else if ((*p & 0xF0) == 0xE0 && p[1] && p[2]) {
            cp = ((unsigned)(p[0] & 0x0F) << 12) | ((unsigned)(p[1] & 0x3F) << 6) |
                 (p[2] & 0x3F);
            p += 3;
        } else {
            out[n++] = '?';        /* 4 字节（emoji 之类）GBK 没有，画问号 */
            p++;
            continue;
        }
        {
            int found = 0;

            for (int i = 0; i < GBK_MAP_SIZE; i++) {
                if (gbk_uni_map[i] != (uint16_t)cp) continue;
                {
                    int lead = GBK_LEAD_MIN + i / GBK_TRAIL_COUNT;
                    int trail = GBK_TRAIL_MIN + i % GBK_TRAIL_COUNT;

                    if (n + 2 > cap - 1) return n;
                    out[n++] = (char)lead;
                    out[n++] = (char)trail;
                }
                found = 1;
                break;
            }
            if (!found && n < cap - 1) out[n++] = '?';
        }
    }
    out[n] = '\0';
    return n;
}

/* 字符串常量：按 GBK/ASCII 字节铺进寄存器（每寄存器 2 字节，ABCD 大端） */
static void put_string(sim_dev_t* s, int addr, const char* text)
{
    char gbk[96];
    size_t n;

    if (!text) text = "";
    n = (size_t)utf8_to_gbk(text, gbk, (int)sizeof(gbk));
    for (size_t i = 0; i < n; i += 2) {
        int hi = (unsigned char)gbk[i];
        int lo = (i + 1 < n) ? (unsigned char)gbk[i + 1] : 0;
        int reg = addr + (int)i / 2;

        if (reg < 0 || reg >= SIM_HOLDING_MAX) break;
        s->holding[reg] = (uint16_t)((hi << 8) | lo);
    }
}

static int load_json(const project_t* p, sim_dev_t* sim)
{
    char path[320];
    char* text;
    size_t len = 0;
    hmi_json_t* root;
    const hmi_json_t* gens;
    const hmi_json_t* consts;
    size_t n;
    char jerr[128] = { 0 };

    snprintf(path, sizeof(path), "%s/sim.json", p->root);
    text = read_text(path, &len);
    if (!text) return 0;                       /* 没有这个文件是正常的 */
    root = hmi_json_parse(text, len, jerr, sizeof(jerr));
    if (!root) {
        set_err("sim.json 解析失败：%s（跳过声明式生成器）", jerr);
        free(text);
        return -1;
    }
    s_tick_ms = (uint32_t)hmi_json_int(hmi_json_get(root, "tick_ms"), 100);
    if (s_tick_ms < 10) s_tick_ms = 10;
    if (s_tick_ms > 10000) s_tick_ms = 10000;
    s_gen_tick = s_tick_ms;

    consts = hmi_json_get(root, "consts");
    for (size_t i = 0; i < hmi_json_count(consts); i++) {
        const hmi_json_t* c = hmi_json_at(consts, i);
        int area = area_from_name(hmi_json_str(hmi_json_get(c, "area"), "holding"));
        int addr = (int)hmi_json_int(hmi_json_get(c, "addr"), -1);
        const char* txt = hmi_json_str(hmi_json_get(c, "text"), "");

        if (area < 0 || addr < 0) {
            set_err("sim.json consts[%zu] 的 area/addr 非法（跳过该条）", i);
            continue;
        }
        put_string(sim, addr, txt);
    }

    gens = hmi_json_get(root, "gens");
    n = hmi_json_count(gens);
    for (size_t i = 0; i < n && s_ngens < SIMCFG_MAX_GENS; i++) {
        const hmi_json_t* g = hmi_json_at(gens, i);
        sim_gen_t* d = &s_gens[s_ngens];
        const char* wrap = hmi_json_str(hmi_json_get(g, "wrap"), "reset");

        memset(d, 0, sizeof(*d));
        d->area = area_from_name(hmi_json_str(hmi_json_get(g, "area"), "holding"));
        d->addr = (int)hmi_json_int(hmi_json_get(g, "addr"), -1);
        d->datatype = dt_from_name(hmi_json_str(hmi_json_get(g, "datatype"), NULL));
        if (d->area < 0 || d->addr < 0 || d->datatype < 0 || d->datatype == DT_STRING) {
            set_err("sim.json gens[%zu] 的 area/addr/datatype 非法（跳过该条）", i);
            continue;
        }
        d->init = (double)hmi_json_int(hmi_json_get(g, "init"), 0);
        d->step = hmi_json_int(hmi_json_get(g, "step"), 1);
        d->min = hmi_json_int(hmi_json_get(g, "min"), 0);
        d->max = hmi_json_int(hmi_json_get(g, "max"), 65535);
        d->period_ms = (uint32_t)hmi_json_int(hmi_json_get(g, "period_ms"), (long)s_gen_tick);
        if (d->period_ms < 10) d->period_ms = 10;
        d->wrap = (strcmp(wrap, "zero") == 0) ? 1 : (strcmp(wrap, "hold") == 0) ? 2 : 0;
        sim_write(sim, d->area, d->addr, d->datatype, d->init, 0);
        s_ngens++;
    }
    hmi_json_free(root);
    free(text);
    if (s_ngens) HMI_LOG("sim: sim.json 载入 %d 个生成器（节拍 %ums）", s_ngens, s_tick_ms);
    return 0;
}

static void gens_tick(sim_dev_t* sim, uint32_t elapsed_ms)
{
    for (int i = 0; i < s_ngens; i++) {
        sim_gen_t* g = &s_gens[i];
        double v, num = 0;
        int b = 0;

        g->acc_ms += elapsed_ms;
        if (g->acc_ms < g->period_ms) continue;
        g->acc_ms %= g->period_ms;
        if (sim_read(sim, g->area, g->addr, g->datatype, &num, &b) != 0) continue;
        v = num + g->step;
        if (v > g->max) {
            v = (g->wrap == 1) ? 0.0 : (g->wrap == 2) ? g->max : g->init;
        } else if (v < g->min) {
            v = (g->wrap == 1) ? 0.0 : (g->wrap == 2) ? g->min : g->init;
        }
        sim_write(sim, g->area, g->addr, g->datatype, v, 0);
    }
}

/* ── sim.lua ── */

static void budget_hook(lua_State* L, lua_Debug* ar)
{
    (void)ar;
    s_abort = 1;
    luaL_error(L, "超出单拍指令预算（%d）", SIMCFG_BUDGET);
}

static int l_sim_set(lua_State* L)
{
    int area = area_from_name(luaL_checkstring(L, 1));
    int addr = (int)luaL_checkinteger(L, 2);
    int dt = dt_from_name(luaL_checkstring(L, 3));
    double v;
    int b;

    if (area < 0 || dt < 0) return luaL_error(L, "sim.set: area/datatype 非法");
    if (dt == DT_BOOL) {
        b = lua_toboolean(L, 4);
        v = b ? 1.0 : 0.0;
    } else {
        v = luaL_checknumber(L, 4);
        b = 0;
    }
    if (sim_write(s_sim, area, addr, dt, v, b) != 0)
        return luaL_error(L, "sim.set: 地址越界或只读（%s %d）", area_name(area), addr);
    return 0;
}

static int l_sim_get(lua_State* L)
{
    int area = area_from_name(luaL_checkstring(L, 1));
    int addr = (int)luaL_checkinteger(L, 2);
    int dt = dt_from_name(luaL_checkstring(L, 3));
    double num = 0;
    int b = 0;

    if (area < 0 || dt < 0) return luaL_error(L, "sim.get: area/datatype 非法");
    if (sim_read(s_sim, area, addr, dt, &num, &b) != 0)
        return luaL_error(L, "sim.get: 地址越界（%s %d）", area_name(area), addr);
    if (dt == DT_BOOL) lua_pushboolean(L, b);
    else lua_pushnumber(L, num);
    return 1;
}

static int l_sim_set_tag(lua_State* L)
{
    int id = (int)luaL_checkinteger(L, 1);
    const tag_def_t* t = s_proj ? project_tag(s_proj, id) : NULL;

    if (!t) return luaL_error(L, "sim.set_tag: 变量 %d 不存在", id);
    if (t->type == TAG_T_BOOL) return sim_write_tag(s_sim, t, 0.0, lua_toboolean(L, 2)) == 0
                                       ? 0 : luaL_error(L, "sim.set_tag: 变量 %d 写失败", id);
    return sim_write_tag(s_sim, t, luaL_checknumber(L, 2), 0) == 0
               ? 0 : luaL_error(L, "sim.set_tag: 变量 %d 写失败", id);
}

static int l_sim_get_tag(lua_State* L)
{
    int id = (int)luaL_checkinteger(L, 1);
    const tag_def_t* t = s_proj ? project_tag(s_proj, id) : NULL;
    double num = 0;
    int b = 0;

    if (!t) return luaL_error(L, "sim.get_tag: 变量 %d 不存在", id);
    if (sim_read(s_sim, t->src_area, t->src_addr, t->src_datatype, &num, &b) != 0)
        return luaL_error(L, "sim.get_tag: 变量 %d 读不到（不是 sim 设备）", id);
    if (t->type == TAG_T_BOOL) lua_pushboolean(L, b);
    else lua_pushnumber(L, num);
    return 1;
}

static int l_sim_log(lua_State* L)
{
    const char* s = luaL_checkstring(L, 1);

    HMI_LOG("sim.lua: %s", s);
    return 0;
}

static void lua_fail(const char* what, const char* msg)
{
    set_err("%s：%s（已停用 sim.lua，界面照常运行）", what, msg ? msg : "未知错误");
    s_disabled = 1;
}

static int load_lua(const project_t* p)
{
    char path[320];
    static const luaL_Reg api[] = {
        { "set", l_sim_set },
        { "get", l_sim_get },
        { "set_tag", l_sim_set_tag },
        { "get_tag", l_sim_get_tag },
        { "log", l_sim_log },
        { NULL, NULL },
    };

    snprintf(path, sizeof(path), "%s/sim.lua", p->root);
    {
        FILE* f = fopen(path, "rb");

        if (!f) return 0;                      /* 没有脚本是正常的 */
        fclose(f);
    }

    s_L = luaL_newstate();
    if (!s_L) {
        set_err("sim.lua：建 lua_State 失败（跳过）");
        return -1;
    }
    /* 只开 base/table/string/math（与 lua_rt 同口径：linit.c 没编进来，不能用 luaL_openlibs）。
     * os/io/package/debug 从一开始就不存在；math.random 摘掉，保证 golden 可复现。 */
    luaL_requiref(s_L, "_G", luaopen_base, 1);
    lua_pop(s_L, 1);
    luaL_requiref(s_L, LUA_TABLIBNAME, luaopen_table, 1);
    lua_pop(s_L, 1);
    luaL_requiref(s_L, LUA_STRLIBNAME, luaopen_string, 1);
    lua_pop(s_L, 1);
    luaL_requiref(s_L, LUA_MATHLIBNAME, luaopen_math, 1);
    lua_pop(s_L, 1);
    {
        static const char* const drop[] = { "os", "io", "package", "debug", "dofile",
                                            "loadfile", "load", "require", "collectgarbage" };

        for (size_t k = 0; k < sizeof(drop) / sizeof(drop[0]); k++) {
            lua_pushnil(s_L);
            lua_setglobal(s_L, drop[k]);
        }
        lua_getglobal(s_L, LUA_MATHLIBNAME);
        if (lua_istable(s_L, -1)) {
            lua_pushnil(s_L);
            lua_setfield(s_L, -2, "random");
            lua_pushnil(s_L);
            lua_setfield(s_L, -2, "randomseed");
        }
        lua_pop(s_L, 1);
    }

    lua_newtable(s_L);
    luaL_setfuncs(s_L, api, 0);
    lua_setglobal(s_L, "sim");

    if (luaL_loadfile(s_L, path) != LUA_OK) {
        lua_fail("sim.lua 语法错误", lua_tostring(s_L, -1));
        lua_close(s_L);
        s_L = NULL;
        return -1;
    }
    if (lua_pcall(s_L, 0, 0, 0) != LUA_OK) {
        lua_fail("sim.lua 执行失败", lua_tostring(s_L, -1));
        lua_close(s_L);
        s_L = NULL;
        return -1;
    }
    lua_getglobal(s_L, "on_tick");
    s_lua_on_tick = lua_isfunction(s_L, -1);
    lua_pop(s_L, 1);
    lua_getglobal(s_L, "on_write");
    s_lua_on_write = lua_isfunction(s_L, -1);
    lua_pop(s_L, 1);

    lua_getglobal(s_L, "on_start");
    if (lua_isfunction(s_L, -1)) {
        s_abort = 0;
        lua_sethook(s_L, budget_hook, LUA_MASKCOUNT, SIMCFG_BUDGET);
        if (lua_pcall(s_L, 0, 0, 0) != LUA_OK) lua_fail("sim.lua on_start 失败", lua_tostring(s_L, -1));
    } else {
        lua_pop(s_L, 1);        /* 不是函数才需要弹；pcall 成功时函数已被它弹出 */
    }
    lua_sethook(s_L, NULL, 0, 0);
    if (!s_disabled)
        HMI_LOG("sim: sim.lua 就绪（on_tick=%d on_write=%d）", s_lua_on_tick, s_lua_on_write);
    return 0;
}

/* ── 对外 ── */

int sim_cfg_load(const project_t* p, sim_dev_t* sim, char* err, size_t errsz)
{
    s_proj = p;
    s_sim = sim;
    s_ngens = 0;
    s_disabled = 0;
    s_acc_ms = 0;
    s_have_err = 0;
    s_err[0] = '\0';

    if (!p) return 0;
    (void)load_json(p, sim);
    (void)load_lua(p);
    if (s_have_err && err && errsz) snprintf(err, errsz, "%s", s_err);
    return 0;                                   /* 模拟出问题不影响启动 */
}

void sim_cfg_tick(sim_dev_t* sim, uint32_t now_ms)
{
    static uint32_t last_ms;
    uint32_t dt;

    if (!sim) return;
    if (last_ms == 0) last_ms = now_ms;
    dt = now_ms - last_ms;
    if (dt < s_tick_ms) return;
    last_ms = now_ms;

    if (s_ngens) gens_tick(sim, dt);
    if (!s_L || s_disabled || !s_lua_on_tick) return;

    lua_getglobal(s_L, "on_tick");
    lua_pushinteger(s_L, (lua_Integer)now_ms);
    lua_pushinteger(s_L, (lua_Integer)dt);
    s_abort = 0;
    lua_sethook(s_L, budget_hook, LUA_MASKCOUNT, SIMCFG_BUDGET);
    if (lua_pcall(s_L, 2, 0, 0) != LUA_OK) lua_fail("sim.lua on_tick 失败", lua_tostring(s_L, -1));
    lua_sethook(s_L, NULL, 0, 0);
}

void sim_cfg_on_write(int tag, double value)
{
    if (!s_L || s_disabled || !s_lua_on_write) return;
    lua_getglobal(s_L, "on_write");
    lua_pushinteger(s_L, (lua_Integer)tag);
    lua_pushnumber(s_L, (lua_Number)value);
    s_abort = 0;
    lua_sethook(s_L, budget_hook, LUA_MASKCOUNT, SIMCFG_BUDGET);
    if (lua_pcall(s_L, 2, 0, 0) != LUA_OK) lua_fail("sim.lua on_write 失败", lua_tostring(s_L, -1));
    lua_sethook(s_L, NULL, 0, 0);
}
