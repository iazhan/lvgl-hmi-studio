/*
 * cg_api.h — 脚本侧 cg.* 命名空间与「全局变量即变量」（37 §7.1/§7.2）
 *
 * 装上之后：
 *   · 脚本里写 `温度` 就是读 tagdb 里名为「温度」的变量，赋值就是写它
 *   · cg.getTag(name) / cg.setTag(name, v) 覆盖含点、数字开头等非标识符名字
 *   · cg.log / cg.openScreen / cg.closeScreen / cg.isScreenOpen
 *
 * 变量名查找是线性扫描（工程变量上限 2000），脚本访问频率低，够用；
 * 真成瓶颈再换哈希。
 */
#ifndef HMI_CG_API_H
#define HMI_CG_API_H

#include "project/loader.h"

/* 绑定工程并注册 cg 表与 _G 的 metatable。需要在 lua_rt_init 之后调用。 */
int  cg_api_attach(const project_t* p);

#endif /* HMI_CG_API_H */
