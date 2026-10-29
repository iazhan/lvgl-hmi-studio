/*
 * lua_rt.h — Lua 5.4.7 运行时的宿主侧封装（37 §7）
 *
 * 只做三件事：建一个受控的 lua_State、按白名单开库、带指令限额地执行一段脚本。
 * 与组态的接口（cg.*）在 cg_api.c 里注册，这里不掺业务。
 */
#ifndef HMI_LUA_RT_H
#define HMI_LUA_RT_H

#include <stddef.h>

struct lua_State;

/* 建 state 并开沙箱库。重复调用无副作用；失败返回 -1。 */
int  lua_rt_init(void);

void lua_rt_close(void);

/* 取内部 state（cg_api 注册函数时用）。未初始化时返回 NULL。 */
struct lua_State* lua_rt_state(void);

/* 编译并执行一段脚本（不带返回值）。成功 0，失败 -1 并写 err（含行号）。 */
int  lua_rt_dostring(const char* code, const char* chunk, char* err, size_t errsz);

/* 执行一段脚本并把第一个返回值格式化成文字（自检用）。 */
int  lua_rt_eval_str(const char* code, char* out, size_t outsz,
                     char* err, size_t errsz);

/* 编译并执行一段脚本（用于策略源码：执行后全局里留下函数），不取返回值。 */
int  lua_rt_run(const char* code, const char* chunk, char* err, size_t errsz);

/* 取一个全局函数并存成引用（策略每周期调用它）。成功 0，返回引用号。 */
int  lua_rt_ref_global_fn(const char* name, int* ref, char* err, size_t errsz);

/* 调用一个已保存的函数引用。 */
int  lua_rt_call_ref(int ref, char* err, size_t errsz);

/* 单脚本单次执行的指令上限（37 §7.3：200k） */
#define LUA_RT_INSN_LIMIT 200000

#endif /* HMI_LUA_RT_H */
