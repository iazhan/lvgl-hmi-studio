/*
 * ui_binding.h — 属性绑定表（v1：fmt 与直接取值）
 *
 * 页面进入时生成绑定表，每项记：tag id、目标控件、属性、kind、上次 version。
 * 刷新定时器（约 100ms，34 §5.7）比对 version，有变化才求值并写控件——
 * 开销与当前页绑定数成正比，与总变量数无关。
 *
 * kind：直接取值 / fmt / enum / ranges / bit / expr（37 §6.3 的六种）。
 */
#ifndef HMI_UI_BINDING_H
#define HMI_UI_BINDING_H

#include "widgets.h"
#include "expr.h"
#include "hmi_json.h"

typedef enum {
    BIND_TEXT = 0,      /* text 属性：label/value/enum_text 的文字 */
    BIND_VISIBLE,       /* visible 属性：显隐 */
    BIND_VALUE,         /* value 属性：bar/gauge/slider 的读数 */
    BIND_CHECKED,       /* checked 属性：switch 的通断 */
    BIND_COLOR,         /* color 属性：lamp/bar 的状态颜色（enum 或 ranges） */
    BIND_BG_COLOR,      /* bg_color 属性：背景色（同样支持 enum / ranges） */
    BIND_ENABLED        /* enabled 属性：可交互 / 置灰 */
} bind_prop_t;

typedef enum {
    BIND_DIRECT = 0,    /* 不写 kind：直接取值 */
    BIND_FMT,           /* kind=fmt：按 fmt 格式化 */
    BIND_ENUM,          /* kind=enum：数值映射成文字（可带颜色） */
    BIND_RANGES,        /* kind=ranges：按区间映射成颜色 */
    BIND_BIT,           /* kind=bit：取变量的某一位 */
    BIND_EXPR           /* kind=expr：只读表达式（37 §6.3） */
} bind_kind_t;

#define BIND_MAP_MAX 8

typedef struct {
    double v;
    char   text[20];
    char   color[8];        /* "#RRGGBB"，空表示不指定 */
} bind_enum_t;

#define BIND_RANGE_MAX 8

typedef struct {
    double lt;              /* 上界；has_lt=0 表示"其余情况"，只允许出现在最后一项 */
    int    has_lt;
    char   color[8];
} bind_range_t;

typedef struct {
    int tag_id;
    bind_prop_t prop;
    bind_kind_t kind;
    char fmt[24];           /* kind=fmt 的格式串（如 "%.1f"） */
    bind_enum_t map[BIND_MAP_MAX];   /* kind=enum 的映射表 */
    int  map_count;
    bind_range_t ranges[BIND_RANGE_MAX];   /* kind=ranges 的区间表 */
    int  range_count;
    int  bit;               /* kind=bit 的位号 */
    int  cell_r, cell_c;    /* table 单元格绑定：-1 表示不是单元格 */
    expr_prog_t prog;       /* kind=expr 编译后的 AST */
    int  expr_err_logged;   /* 求值错误只记一行日志 */
    int  text_err_logged;   /* 「该控件不支持 text 绑定」只记一次 */
    uint32_t last_version;  /* 上次应用时的 tag version；初值取不可能值，保证首帧必刷 */
    widget_t* w;
} binding_t;

/* 建整页绑定表：widgets_json 为页面 widgets 数组，widgets 为**同序**建好的
 * 控件（第 i 个控件对应 widgets[i]）。失败写 err（含控件 id 与原因）返回 -1。 */
int  bind_table_build(binding_t** out, int* count, const hmi_json_t* const* nodes,
                      widget_t* widgets, int nwidgets, char* err, size_t errsz);

void bind_table_free(binding_t* items, int count);

/* 版本比对 → 求值 → 应用到控件 */
void bind_table_refresh(binding_t* items, int count);

#endif /* HMI_UI_BINDING_H */
