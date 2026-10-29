/*
 * lua_rt.c — Lua 5.4.7 运行时的宿主侧封装（37 §7）
 *
 * 沙箱策略（37 §7.3）：
 *   · 允许 base / coroutine / table / string / math / utf8
 *   · 去掉 load、loadfile、dofile、collectgarbage、require、print
 *   · os 只留 time / clock / difftime（date 后置：要自己实现 strftime 的格式子集）
 *   · io / package / debug 根本不打开
 *   · 单次执行 200k 条指令，超限中止并报错
 *
 * 注意：Lua 与界面同线程执行，脚本里不能阻塞——这一条靠 cg.* 的接口设计保证
 * （写设备只入队），不在这里拦。
 */
#include "lua_rt.h"

#include "hmi_log.h"

#include <stdio.h>
#include <string.h>
#include <time.h>

#include "lauxlib.h"
#include "lua.h"
#include "lualib.h"

static lua_State* s_L;

/* 指令计数钩子：到限额就抛错，由 pcall 接住 */
static void insn_hook(lua_State* L, lua_Debug* ar)
{
    (void)ar;
    luaL_error(L, "脚本超过 %d 条指令上限（疑似死循环）", LUA_RT_INSN_LIMIT);
}

static int os_time(lua_State* L)
{
    lua_pushinteger(L, (lua_Integer)time(NULL));
    return 1;
}

static int os_clock(lua_State* L)
{
    lua_pushnumber(L, (lua_Number)((double)clock() / (double)CLOCKS_PER_SEC));
    return 1;
}

static int os_difftime(lua_State* L)
{
    lua_Number a = luaL_checknumber(L, 1);
    lua_Number b = luaL_optnumber(L, 2, 0);

    lua_pushnumber(L, a - b);
    return 1;
}

static const luaL_Reg s_os_funcs[] = {
    { "time", os_time },
    { "clock", os_clock },
    { "difftime", os_difftime },
    { NULL, NULL }
};

int lua_rt_init(void)
{
    if (s_L) return 0;

    s_L = luaL_newstate();
    if (!s_L) {
        HMI_LOG("lua: 创建 state 失败（内存不足）");
        return -1;
    }
    luaL_requiref(s_L, "_G", luaopen_base, 1);
    lua_pop(s_L, 1);
    luaL_requiref(s_L, LUA_TABLIBNAME, luaopen_table, 1);
    lua_pop(s_L, 1);
    luaL_requiref(s_L, LUA_STRLIBNAME, luaopen_string, 1);
    lua_pop(s_L, 1);
    luaL_requiref(s_L, LUA_MATHLIBNAME, luaopen_math, 1);
    lua_pop(s_L, 1);
    luaL_requiref(s_L, LUA_UTF8LIBNAME, luaopen_utf8, 1);
    lua_pop(s_L, 1);
    luaL_requiref(s_L, LUA_COLIBNAME, luaopen_coroutine, 1);
    lua_pop(s_L, 1);

    /* 去掉能逃出沙箱或让脚本卡住的入口 */
    {
        static const char* const drop[] = {
            "load", "loadfile", "dofile", "collectgarbage", "require", "print"
        };

        for (size_t i = 0; i < sizeof(drop) / sizeof(drop[0]); i++) {
            lua_pushnil(s_L);
            lua_setglobal(s_L, drop[i]);
        }
    }

    /* os：自己搭表，避免 luaopen_os 把 execute/remove/exit 一起带进来 */
    luaL_newlib(s_L, s_os_funcs);
    lua_setglobal(s_L, "os");

    lua_sethook(s_L, insn_hook, LUA_MASKCOUNT, LUA_RT_INSN_LIMIT);
    return 0;
}

struct lua_State* lua_rt_state(void)
{
    return s_L;
}

void lua_rt_close(void)
{
    if (!s_L) return;
    lua_close(s_L);
    s_L = NULL;
}

int lua_rt_dostring(const char* code, const char* chunk, char* err, size_t errsz)
{
    int rc;
    const char* name = chunk ? chunk : "=(chunk)";

    if (lua_rt_init() != 0) {
        snprintf(err, errsz, "Lua 运行时初始化失败");
        return -1;
    }
    rc = luaL_loadbuffer(s_L, code, strlen(code), name);
    if (rc != LUA_OK) {
        snprintf(err, errsz, "%s: %s", name, lua_tostring(s_L, -1));
        lua_pop(s_L, 1);
        return -1;
    }
    rc = lua_pcall(s_L, 0, 0, 0);
    if (rc != LUA_OK) {
        snprintf(err, errsz, "%s: %s", name, lua_tostring(s_L, -1));
        lua_pop(s_L, 1);
        return -1;
    }
    return 0;
}

int lua_rt_eval_str(const char* code, char* out, size_t outsz,
                    char* err, size_t errsz)
{
    int rc;
    const char* name = "=(eval)";

    if (lua_rt_init() != 0) {
        snprintf(err, errsz, "Lua 运行时初始化失败");
        return -1;
    }
    rc = luaL_loadbuffer(s_L, code, strlen(code), name);
    if (rc != LUA_OK) {
        snprintf(err, errsz, "%s", lua_tostring(s_L, -1));
        lua_pop(s_L, 1);
        return -1;
    }
    rc = lua_pcall(s_L, 0, 1, 0);
    if (rc != LUA_OK) {
        snprintf(err, errsz, "%s", lua_tostring(s_L, -1));
        lua_pop(s_L, 1);
        return -1;
    }
    switch (lua_type(s_L, -1)) {
    case LUA_TNIL:     snprintf(out, outsz, "nil"); break;
    case LUA_TBOOLEAN: snprintf(out, outsz, lua_toboolean(s_L, -1) ? "true" : "false"); break;
    case LUA_TNUMBER:  snprintf(out, outsz, "%.10g", (double)lua_tonumber(s_L, -1)); break;
    case LUA_TSTRING:  snprintf(out, outsz, "%s", lua_tostring(s_L, -1)); break;
    default:           snprintf(out, outsz, "<%s>", luaL_typename(s_L, -1)); break;
    }
    lua_pop(s_L, 1);
    return 0;
}

int lua_rt_run(const char* code, const char* chunk, char* err, size_t errsz)
{
    return lua_rt_dostring(code, chunk, err, errsz);
}

int lua_rt_ref_global_fn(const char* name, int* ref, char* err, size_t errsz)
{
    if (lua_rt_init() != 0) {
        snprintf(err, errsz, "Lua 运行时初始化失败");
        return -1;
    }
    lua_getglobal(s_L, name);
    if (!lua_isfunction(s_L, -1)) {
        snprintf(err, errsz, "策略源码没有定义函数 %s()", name);
        lua_pop(s_L, 1);
        return -1;
    }
    *ref = luaL_ref(s_L, LUA_REGISTRYINDEX);
    return 0;
}

int lua_rt_call_ref(int ref, char* err, size_t errsz)
{
    int rc;

    if (lua_rt_init() != 0) {
        snprintf(err, errsz, "Lua 运行时初始化失败");
        return -1;
    }
    lua_rawgeti(s_L, LUA_REGISTRYINDEX, ref);
    rc = lua_pcall(s_L, 0, 0, 0);
    if (rc != LUA_OK) {
        snprintf(err, errsz, "%s", lua_tostring(s_L, -1));
        lua_pop(s_L, 1);
        return -1;
    }
    return 0;
}
