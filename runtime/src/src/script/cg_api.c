/*
 * cg_api.c — cg.* 与「全局变量即变量」（37 §7.1/§7.2）
 *
 * 写路径与动作完全一致（page_mgr_write_tag → Tag DB 校验 → sim/Modbus 下发），
 * 所以脚本绕不过只读保护与量程截断。
 */
#include "cg_api.h"

#include "comm/modbus.h"
#include "core/alarm.h"
#include "core/auth.h"
#include "core/i18n.h"
#include "core/tagdb.h"
#include "data/savecfg.h"
#include "hmi_log.h"
#include "lua_rt.h"
#include "ui/ui_page.h"

#include <strings.h>
#include <string.h>

#include "lauxlib.h"
#include "lua.h"

static const project_t* s_proj;

static int find_tag(const char* name)
{
    if (!s_proj || !name || !*name) return -1;
    for (int i = 0; i < s_proj->tags_count; i++)
        if (strcmp(s_proj->tags[i].name, name) == 0) return i;
    return -1;
}

static void push_tag(lua_State* L, int id)
{
    if (s_proj->tags[id].type == TAG_T_BOOL) {
        lua_pushboolean(L, tagdb_bool(id));
    } else if (tagdb_quality(id) != TAG_Q_GOOD) {
        /* 质量不好时给 nil：脚本里 `if 炉温 then` 自然不成立，比拿到旧值安全 */
        lua_pushnil(L);
    } else {
        lua_pushnumber(L, tagdb_num(id));
    }
}

static int write_tag(lua_State* L, int id, int idx)
{
    int is_bool = lua_isboolean(L, idx);
    int b = lua_toboolean(L, idx);
    double n = lua_tonumber(L, idx);

    if (page_mgr_write_tag(id, is_bool, b, n) != 0) {
        return luaL_error(L, "变量 %s 写失败（只读或越界）", s_proj->tags[id].name);
    }
    return 0;
}

static int l_getTag(lua_State* L)
{
    const char* name = luaL_checkstring(L, 1);
    int id = find_tag(name);

    if (id < 0) {
        lua_pushnil(L);
        return 1;
    }
    push_tag(L, id);
    return 1;
}

static int l_setTag(lua_State* L)
{
    const char* name = luaL_checkstring(L, 1);
    int id = find_tag(name);

    if (id < 0) return luaL_error(L, "变量 %s 不存在", name);
    lua_settop(L, 2);
    return write_tag(L, id, 2);
}

static int l_log(lua_State* L)
{
    const char* msg = luaL_checkstring(L, 1);

    HMI_LOG("lua: %s", msg);
    return 0;
}

static int l_openScreen(lua_State* L)
{
    page_mgr_script_open(luaL_checkstring(L, 1));
    return 0;
}

static int l_closeScreen(lua_State* L)
{
    (void)L;
    page_mgr_script_close();
    return 0;
}

static int l_isScreenOpen(lua_State* L)
{
    lua_pushboolean(L, page_mgr_script_is_open(luaL_checkstring(L, 1)));
    return 1;
}

/* cg.setDevice(device, mode, area, addr, type[, count], value)
 * v1 只支持写模式：Read 类要同步读，而我们所有读都是轮询缓存，硬做会阻塞界面。 */
static int l_setDevice(lua_State* L)
{
    const char* dev = luaL_checkstring(L, 1);
    const char* mode = luaL_checkstring(L, 2);
    const char* area_s = luaL_checkstring(L, 3);
    int addr = (int)luaL_checkinteger(L, 4);
    const char* type_s = luaL_checkstring(L, 5);
    int area = page_mgr_area_from_name(area_s);
    int dt = page_mgr_dt_from_name(type_s);
    int is_bool, rc;
    double n;

    if (!(strcasecmp(mode, "Write") == 0 || strcasecmp(mode, "WriteP") == 0 ||
          strcasecmp(mode, "WritePV") == 0)) {
        return luaL_error(L, "cg.setDevice: v1 只支持写模式（Read 类要同步读，未实现）");
    }
    if (area < 0) return luaL_error(L, "cg.setDevice: area %s 非法", area_s);
    if (area != AREA_COIL && area != AREA_HOLDING) {
        return luaL_error(L, "cg.setDevice: area %s 是只读区，不能写", area_s);
    }
    if (dt < 0 || dt == DT_I64 || dt == DT_DOUBLE || dt == DT_STRING) {
        return luaL_error(L, "cg.setDevice: datatype %s 暂不支持（v1 只到 32 位）", type_s);
    }
    if (lua_isnumber(L, 7) || lua_isboolean(L, 7)) {
        if ((int)lua_tointeger(L, 6) != 1) {
            return luaL_error(L, "cg.setDevice: v1 一次只写 1 个值（count 必须为 1）");
        }
        is_bool = lua_isboolean(L, 7);
        n = lua_tonumber(L, 7);
    } else if (lua_isnumber(L, 6) || lua_isboolean(L, 6)) {
        is_bool = lua_isboolean(L, 6);
        n = lua_tonumber(L, 6);
    } else {
        return luaL_error(L, "cg.setDevice: 缺写入值");
    }
    rc = page_mgr_write_device(dev, area, addr, dt, n, is_bool);
    if (rc != 0) return luaL_error(L, "cg.setDevice: 写设备 %s 失败", dev);
    return 0;
}

static int l_startDevice(lua_State* L)
{
    const char* dev = luaL_checkstring(L, 1);

    if (modbus_device_enable(s_proj, dev, 1) != 0)
        return luaL_error(L, "cg.startDevice: 设备 %s 不存在", dev);
    return 0;
}

static int l_stopDevice(lua_State* L)
{
    const char* dev = luaL_checkstring(L, 1);

    if (modbus_device_enable(s_proj, dev, 0) != 0)
        return luaL_error(L, "cg.stopDevice: 设备 %s 不存在", dev);
    return 0;
}

/* 下面几个依赖还没开发的子系统。明确报错，不装作成功——静默失败的脚本最难查。 */
static int l_saveNow(lua_State* L)
{
    const char* g = luaL_optstring(L, 1, NULL);
    int n;

    if (!auth_perm(PERM_CHANGE_CONFIG))
        return luaL_error(L, "cg.saveNow: 当前用户没有 change_config 权限");
    if (!g || !*g) {
        n = savecfg_sample_now(NULL);
    } else {
        n = savecfg_sample_now(g);
        if (n < 0) return luaL_error(L, "cg.saveNow: 没有存盘组 %s", g);
    }
    if (n < 0) return luaL_error(L, "cg.saveNow: 没有存盘组（savecfg.json）");
    return 0;
}

/* 导出存盘组为 CSV 到 U 盘：cg.exportDatalog(group[, last_min])，返回数据行数 */
static int l_exportDatalog(lua_State* L)
{
    const char* g = luaL_checkstring(L, 1);
    int last_min = (int)luaL_optinteger(L, 2, 0);
    char err[160];
    int rows;

    if (!g || !*g) return luaL_error(L, "cg.exportDatalog: 需要组名");
    if (last_min < 0 || last_min > 10080)
        return luaL_error(L, "cg.exportDatalog: last_min 越界（0..10080）");
    if (!auth_perm(PERM_EXPORT))
        return luaL_error(L, "cg.exportDatalog: 当前用户没有 export 权限");
    rows = hmi_datalog_export(g, last_min, 0, 0, err, sizeof(err));
    if (rows == -2) return luaL_error(L, "cg.exportDatalog: 没插 U 盘");
    if (rows < 0) return luaL_error(L, "cg.exportDatalog: %s", err[0] ? err : "导出失败");
    lua_pushinteger(L, rows);
    return 1;
}

/* 切语言：cg.setLanguage("en")；与 set_language 动作同一套（写设备状态 + 推迟重建当前页） */
static int l_setLanguage(lua_State* L)
{
    const char* code = luaL_checkstring(L, 1);
    char err[96];

    if (!code || !*code) return luaL_error(L, "cg.setLanguage: 需要语言代码");
    if (strcmp(code, i18n_lang()) == 0) return 0;      /* 已经在这个语言：不重建页面 */
    if (i18n_switch(code, err, sizeof(err)) != 0)
        return luaL_error(L, "cg.setLanguage: %s", err);
    page_mgr_request_reload(NULL);
    return 0;
}

/* 确认报警：cg.ackAlarm(rule_id)；id < 0 = 全部。返回确认条数 */
static int l_ackAlarm(lua_State* L)
{
    int id = (int)luaL_checkinteger(L, 1);
    int n;

    if (!auth_perm(PERM_ACK_ALARM))
        return luaL_error(L, "cg.ackAlarm: 当前用户没有 ack_alarm 权限");
    if (id < 0) n = alarm_ack_all(NULL);
    else n = (alarm_ack(id) == 0) ? 1 : 0;
    lua_pushinteger(L, n);
    return 1;
}

/* 搁置报警：cg.shelveAlarm(rule_id, minutes)（46 §8.1）；返回 true 表示已搁置 */
static int l_shelveAlarm(lua_State* L)
{
    int id = (int)luaL_checkinteger(L, 1);
    int mins = (int)luaL_optinteger(L, 2, 30);

    if (!auth_perm(PERM_SHELVE))
        return luaL_error(L, "cg.shelveAlarm: 当前用户没有 shelve 权限");
    lua_pushboolean(L, alarm_shelve(id, mins, auth_user_name()) == 0);
    return 1;
}

static int l_unshelveAlarm(lua_State* L)
{
    int id = (int)luaL_checkinteger(L, 1);

    if (!auth_perm(PERM_SHELVE))
        return luaL_error(L, "cg.unshelveAlarm: 当前用户没有 shelve 权限");
    lua_pushboolean(L, alarm_unshelve(id) == 0);
    return 1;
}

/* 以某个身份登录：cg.registerUser(name[, password])；免密用户直接过。
 * 与登录框同一套判定（失败计数/锁定），页面重建由主循环检测身份变化后做。 */
static int l_registerUser(lua_State* L)
{
    const char* name = luaL_checkstring(L, 1);
    const char* pass = luaL_optstring(L, 2, "");
    int rc = auth_login(name, pass ? pass : "");

    if (rc == -2) return luaL_error(L, "cg.registerUser: %s 失败次数过多，已锁定", name);
    if (rc != 0) return luaL_error(L, "cg.registerUser: 用户名或口令不对");
    return 0;
}

/* 注销当前用户：cg.LogOff()；审计与页面重建同上（rebuild 由主循环做） */
static int l_LogOff(lua_State* L)
{
    (void)L;
    if (!auth_logged_in()) return 0;
    auth_logout();
    return 0;
}

static const luaL_Reg s_cg_funcs[] = {
    { "getTag", l_getTag },
    { "setTag", l_setTag },
    { "log", l_log },
    { "openScreen", l_openScreen },
    { "closeScreen", l_closeScreen },
    { "isScreenOpen", l_isScreenOpen },
    { "setDevice", l_setDevice },
    { "startDevice", l_startDevice },
    { "stopDevice", l_stopDevice },
    { "saveNow", l_saveNow },
    { "exportDatalog", l_exportDatalog },
    { "setLanguage", l_setLanguage },
    { "ackAlarm", l_ackAlarm },
    { "shelveAlarm", l_shelveAlarm },
    { "unshelveAlarm", l_unshelveAlarm },
    { "registerUser", l_registerUser },
    { "LogOff", l_LogOff },
    { NULL, NULL }
};

/* _G 的 __index：读未定义的全局时，先当作变量名查一次 */
static int g_index(lua_State* L)
{
    const char* key = lua_tostring(L, 2);
    int id = key ? find_tag(key) : -1;

    if (id >= 0) {
        push_tag(L, id);
        return 1;
    }
    lua_pushnil(L);
    return 1;
}

/* _G 的 __newindex：赋值给未定义的全局时，若名字是变量就写变量，否则照常建全局 */
static int g_newindex(lua_State* L)
{
    const char* key = lua_tostring(L, 2);
    int id = key ? find_tag(key) : -1;

    if (id >= 0) return write_tag(L, id, 3);
    lua_rawset(L, 1);          /* 普通全局：按 Lua 原语义写进表 */
    return 0;
}

int cg_api_attach(const project_t* p)
{
    lua_State* L = lua_rt_state();

    if (!L || !p) return -1;
    s_proj = p;

    luaL_newlib(L, s_cg_funcs);
    lua_setglobal(L, "cg");

    /* _G 的 metatable：只在 rawget 未命中时触发，所以 math/string 这些不受影响 */
    lua_newtable(L);
    lua_pushcfunction(L, g_index);
    lua_setfield(L, -2, "__index");
    lua_pushcfunction(L, g_newindex);
    lua_setfield(L, -2, "__newindex");

    lua_rawgeti(L, LUA_REGISTRYINDEX, LUA_RIDX_GLOBALS);
    lua_insert(L, -2);          /* metatable 放到 globals 下面 */
    lua_setmetatable(L, -2);
    lua_pop(L, 1);

    HMI_LOG("lua: cg 已就绪（%d 个变量可按名字访问）", p->tags_count);
    return 0;
}
