/*
 * hmi_limits.h — 编译期容量上限（与板型/镜像绑定，不是工程设置）
 *
 * ★ 不要叫 limits.h：-Isrc/core 会把 <limits.h> 也导到这份文件上，Lua 的 luaconf.h
 * 靠 <limits.h> 里的 LLONG_MAX 判断"编译器支不支持 long long"，被顶掉后直接编不过。
 *
 * 为什么放这里而不是各处的 #define：`--caps` 要报这些值，但它刻意不依赖 LVGL 与
 * 工程模型（板端排查时只要能打一行 JSON）。把这个值抽成"两边都能 include 的小头"，
 * caps.c 与 widgets.h 就共用同一份定义，不会各写一个数再靠脚本核对。
 */
#ifndef HMI_LIMITS_H
#define HMI_LIMITS_H

/* 每页控件上限（含容器子控件、页签内容、模板实例展开后的控件）。
 *
 * 它是**镜像级**参数而不是工程级：改大就改静态占用——每个 widget_t 约 1.2KB，
 * 150 → 180KB、300 → 348KB（外加 LVGL 对象）。所以按镜像定，由 --caps 的
 * limits.widgets_per_page 报给编辑器；工程不该自带这个数，否则换个板子就跑不了。
 *
 * 构建时覆盖：make WIDGETS_PER_PAGE=300（见 Makefile）。
 */
#ifndef HMI_WIDGETS_PER_PAGE
#define HMI_WIDGETS_PER_PAGE 150
#endif

#if HMI_WIDGETS_PER_PAGE < 32 || HMI_WIDGETS_PER_PAGE > 512
#error "HMI_WIDGETS_PER_PAGE 超出合理范围 32..512（RAM ≈ 值 × 1.2KB）"
#endif

#endif /* HMI_LIMITS_H */
