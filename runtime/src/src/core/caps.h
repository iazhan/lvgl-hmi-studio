/*
 * caps.h — 本二进制"编进了什么能力"
 *
 * 能力分三层（docs/chenge-rt/46 §1）：板级有没有这个硬件（spec/hw 下按板型一份，编辑器读）、
 * 固件有没有编这个模块（本文件）、工程有没有用到（manifest.features）。
 * 装载期只做最后一道：工程声明了能力但本固件没编，就拒绝加载并说清缺什么——
 * 不静默降级，否则现场表现为"界面在、数据不动"。
 *
 * 模块表是本文件里的唯一定义；spec/features.json 是同一张表的契约副本，
 * tools/check_caps.py 双向核对（golden.sh 里跑）。新增模块必须同时改两处。
 */
#ifndef HMI_CAPS_H
#define HMI_CAPS_H

#include <stdio.h>

/* ── 构建开关 ──
 * 只有真正带条件编译的模块才有开关；未给出的模块按代码的实际状态取常量。
 * 未实现的模块（mqtt/modbus_tcp/s7/anim3）在 Makefile 里打开会直接报错，
 * 免得配了开关却以为能力已在。 */
#ifndef HMI_WITH_SIM
#define HMI_WITH_SIM 1
#endif

/* 模块表：X(id, 编入标志, 一句说明) */
#define HMI_MODULE_LIST(X)                                                  \
    X("sim",        HMI_WITH_SIM, "模拟设备（sim.json / sim.lua）")          \
    X("lua",        1,            "Lua 5.4.7 沙箱（cg.* 与策略）")           \
    X("expr",       1,            "表达式求值")                             \
    X("alarm",      1,            "报警引擎与报警事件")                      \
    X("trend",      1,            "趋势采集与存盘")                          \
    X("recipe",     1,            "配方")                                   \
    X("users",      1,            "用户、权限与登录")                        \
    X("i18n",       1,            "多语言")                                 \
    X("ui_ext",     1,            "扩展控件（表格/曲线/图表/管道等）")         \
    X("anim",       1,            "属性动画（页面级 animations[]）")          \
    X("anim3",      0,            "三维动画（未实现）")                      \
    X("theme",      0,            "主题令牌（不做：编辑器导出时烘焙）")      \
    X("modbus_tcp", 0,            "Modbus TCP 主站（未实现）")               \
    X("mqtt",       0,            "MQTT 客户端（未实现）")                   \
    X("s7",         0,            "西门子 S7 主站（未实现）")

/* 能力表：X(manifest.features 的取值, 依赖模块) */
#define HMI_FEATURE_LIST(X)     \
    X("lua",         "lua")     \
    X("expr",        "expr")    \
    X("alarm",       "alarm")   \
    X("trend",       "trend")   \
    X("recipe",      "recipe")  \
    X("users",       "users")   \
    X("i18n",        "i18n")    \
    X("table",       "ui_ext")  \
    X("curves",      "ui_ext")  \
    X("pipe",        "ui_ext")  \
    X("widgets_ext", "ui_ext")  \
    X("anim",        "anim")    \
    X("anim3",       "anim3")   \
    X("theme",       "theme")   \
    X("modbus_tcp",  "modbus_tcp") \
    X("mqtt",        "mqtt")    \
    X("s7",          "s7")

/* 能力位图的位宽：manifest 用 uint32 存"声明了哪些能力"，
 * 能力总数超过它就必须一起改 loader 的 features_mask 类型。 */
#define HMI_FEATURE_BITS 32

/* 模块是否编入：1 编入 / 0 未编入 / -1 未知模块 */
int caps_module(const char* id);

/* 能力在 HMI_FEATURE_LIST 里的序号（位图用）；未知返回 -1 */
int caps_feature_index(const char* id);

/* 能力是否可用：1 可用 / 0 不可用（依赖的模块没编）/ -1 未知能力 */
int caps_feature(const char* id);

/* 能力依赖的模块名（报错文案用）；未知能力返回 NULL */
const char* caps_feature_module(const char* id);

/* 打印本二进制的能力表（--caps）：版本、板型、模块清单、可用能力、各项上限 */
void caps_print(FILE* out);

#endif /* HMI_CAPS_H */
