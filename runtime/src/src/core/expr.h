/*
 * expr.h — 37 §6.3 的 expr 绑定：只读表达式，编译成 AST 后按变量 version 求值
 *
 * 语法（与规范一致）：
 *   字面量 十进制/十六进制整数、浮点、true/false、带转义的字符串
 *   变量   tag(<id>)，只读
 *   一元   ! - ~
 *   二元   * / % ; + - ; << >> ; < <= > >= ; == != ; & ; ^ ; | ; && ; ||
 *   三元   ? : （右结合）
 *   函数   abs min max clamp round floor ceil sqrt pow sign toint tofloat
 *          tostring strlen substr
 *
 * 限制：嵌套 ≤32、节点 ≤256、字符串字面量 ≤256 字节。没有赋值、循环与副作用。
 */
#ifndef HMI_EXPR_H
#define HMI_EXPR_H

#include <stddef.h>

#define EX_TAG_MAX 16

typedef struct {
    int    op;
    int    a, b, c;      /* 子节点下标，-1 表示没有 */
    double num;
    int    str_off;      /* 字符串在 arena 里的偏移 */
    int    fn;           /* 函数 id（op == N_CALL 时有效） */
} ex_node_t;

typedef struct {
    ex_node_t* nodes;
    int        count;
    char*      arena;
    int        arena_len;
    int        tags[EX_TAG_MAX];   /* 引用到的变量（用于判断是否需要重算） */
    int        tag_count;
} expr_prog_t;

/* 求值结果：数值 / 字符串 / 布尔 */
enum { EX_V_NUM = 0, EX_V_STR, EX_V_BOOL };

typedef struct {
    int         type;
    double      n;
    const char* s;
} ex_val_t;

/* 编译。失败写 err 返回 -1；成功时 out 里的内存由 expr_free 释放。 */
int  expr_compile(const char* src, expr_prog_t* out, char* err, size_t errsz);
void expr_free(expr_prog_t* p);

/* 求值。成功 0，求值错误（除零/类型错/越界）-1——调用方保留上次的值并记一次日志。 */
int  expr_eval(const expr_prog_t* p, ex_val_t* out);

#endif /* HMI_EXPR_H */
