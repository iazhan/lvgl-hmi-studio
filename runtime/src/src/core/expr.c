/*
 * expr.c — expr 绑定的词法/语法/求值（37 §6.3）
 *
 * 设计取舍：
 *   · AST 用扁平数组 + 子节点下标，不用指针——节点数、深度都能在编译期卡住，
 *     拷贝/释放也只是一次 free
 *   · 字符串字面量进一块 arena，节点只存偏移，避免每个节点都带一个 256 字节缓冲
 *   · 求值只读 Tag DB，没有副作用；错误一律返回 -1，由调用方保留上次的值
 */
#include "expr.h"

#include "core/tagdb.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define EX_NODE_MAX  256
#define EX_STR_MAX   256
#define EX_DEPTH_MAX 32
#define EX_ARENA_MAX 2048

/* ── 节点 ── */
enum {
    N_NUM = 0, N_STR, N_BOOL, N_TAG,
    N_NEG, N_NOT, N_BNOT,
    N_MUL, N_DIV, N_MOD, N_ADD, N_SUB,
    N_SHL, N_SHR,
    N_LT, N_LE, N_GT, N_GE, N_EQ, N_NE,
    N_BAND, N_BXOR, N_BOR, N_AND, N_OR, N_TERN, N_CALL
};

/* ── 函数 ── */
enum {
    F_ABS = 0, F_MIN, F_MAX, F_CLAMP, F_ROUND, F_FLOOR, F_CEIL, F_SQRT,
    F_POW, F_SIGN, F_TOINT, F_TOFLOAT, F_TOSTRING, F_STRLEN, F_SUBSTR
};

static const struct {
    const char* name;
    int         argc;      /* 固定参数个数；-1 表示可变（min/max 2..3） */
} s_funcs[] = {
    { "abs", 1 }, { "min", -1 }, { "max", -1 }, { "clamp", 3 },
    { "round", 1 }, { "floor", 1 }, { "ceil", 1 }, { "sqrt", 1 },
    { "pow", 2 }, { "sign", 1 }, { "toint", 1 }, { "tofloat", 1 },
    { "tostring", 1 }, { "strlen", 1 }, { "substr", 3 },
};

/* ── 词法 ── */
enum {
    T_END = 0, T_NUM, T_STR, T_IDENT, T_OP
};

enum {
    O_LP = 1, O_RP, O_COMMA, O_QUESTION, O_COLON,
    O_OR, O_AND, O_BOR, O_BXOR, O_BAND,
    O_EQ, O_NE, O_LT, O_LE, O_GT, O_GE,
    O_SHL, O_SHR, O_ADD, O_SUB, O_MUL, O_DIV, O_MOD,
    O_NOT, O_BNOT
};

typedef struct {
    const char* p;
    int         type;
    double      num;
    char        str[EX_STR_MAX];
    int         op;
} lx_t;

typedef struct {
    lx_t       lx;
    ex_node_t  pool[EX_NODE_MAX];
    int        count;
    char       arena[EX_ARENA_MAX];
    int        arena_len;
    int        depth;
    int        tags[EX_TAG_MAX];
    int        tag_count;
    const char* err;
} parser_t;

static int is_ident0(int c)
{
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
}

static int is_ident(int c)
{
    return is_ident0(c) || (c >= '0' && c <= '9');
}

static int hexval(int c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

/* 词法：返回 0 成功，-1 词法错误 */
static int lex_next(lx_t* l)
{
    const char* p = l->p;

    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;

    if (!*p) {
        l->type = T_END;
        l->p = p;
        return 0;
    }
    if (*p >= '0' && *p <= '9') {
        char* end = NULL;
        double v;

        if (p[0] == '0' && (p[1] == 'x' || p[1] == 'X')) {
            v = (double)strtoll(p + 2, &end, 16);
        } else {
            v = strtod(p, &end);
        }
        if (!end || end == p) return -1;
        l->type = T_NUM;
        l->num = v;
        l->p = end;
        return 0;
    }
    if (*p == '"' || *p == '\'') {
        char q = *p++;
        int n = 0;

        while (*p && *p != q) {
            int c = (unsigned char)*p++;

            if (c == '\\') {
                int e = (unsigned char)*p++;

                switch (e) {
                case 'n': c = '\n'; break;
                case 't': c = '\t'; break;
                case '\\': c = '\\'; break;
                case '\'': c = '\''; break;
                case '"': c = '"'; break;
                case 'u': {
                    int v = 0;

                    for (int i = 0; i < 4; i++) {
                        int h = hexval((unsigned char)*p);

                        if (h < 0) return -1;
                        v = v * 16 + h;
                        p++;
                    }
                    /* 只处理 BMP 且非 0 的情况；超出范围的按 '?' 处理 */
                    if (v <= 0) return -1;
                    if (v < 0x80) {
                        c = v;
                    } else if (v < 0x800) {
                        if (n + 2 >= EX_STR_MAX) return -1;
                        l->str[n++] = (char)(0xC0 | (v >> 6));
                        c = 0x80 | (v & 0x3F);
                    } else {
                        if (n + 3 >= EX_STR_MAX) return -1;
                        l->str[n++] = (char)(0xE0 | (v >> 12));
                        l->str[n++] = (char)(0x80 | ((v >> 6) & 0x3F));
                        c = 0x80 | (v & 0x3F);
                    }
                    break;
                }
                default: return -1;
                }
            }
            if (n + 1 >= EX_STR_MAX) return -1;
            l->str[n++] = (char)c;
        }
        if (*p != q) return -1;
        p++;
        l->str[n] = '\0';
        l->type = T_STR;
        l->p = p;
        return 0;
    }
    if (is_ident0((unsigned char)*p)) {
        int n = 0;

        while (is_ident((unsigned char)*p)) {
            if (n + 1 >= EX_STR_MAX) return -1;
            l->str[n++] = *p++;
        }
        l->str[n] = '\0';
        l->type = T_IDENT;
        l->p = p;
        return 0;
    }

    /* 运算符：先长后短 */
    {
        struct { const char* s; int op; } ops[] = {
            { "<<", O_SHL }, { ">>", O_SHR },
            { "<=", O_LE }, { ">=", O_GE }, { "==", O_EQ }, { "!=", O_NE },
            { "&&", O_AND }, { "||", O_OR },
            { "(", O_LP }, { ")", O_RP }, { ",", O_COMMA },
            { "?", O_QUESTION }, { ":", O_COLON },
            { "|", O_BOR }, { "^", O_BXOR }, { "&", O_BAND },
            { "<", O_LT }, { ">", O_GT },
            { "+", O_ADD }, { "-", O_SUB }, { "*", O_MUL }, { "/", O_DIV },
            { "%", O_MOD }, { "!", O_NOT }, { "~", O_BNOT },
        };

        for (size_t i = 0; i < sizeof(ops) / sizeof(ops[0]); i++) {
            size_t n = strlen(ops[i].s);

            if (strncmp(p, ops[i].s, n) == 0) {
                l->type = T_OP;
                l->op = ops[i].op;
                l->p = p + n;
                return 0;
            }
        }
    }
    return -1;
}

/* ── 语法 ── */
static int new_node(parser_t* ps, int op)
{
    if (ps->count >= EX_NODE_MAX) return -1;
    {
        ex_node_t* n = &ps->pool[ps->count];

        memset(n, 0, sizeof(*n));
        n->op = op;
        n->a = n->b = n->c = -1;
        return ps->count++;
    }
}

static int parse_expr(parser_t* ps);

static void expect(parser_t* ps)
{
    if (lex_next(&ps->lx) != 0) ps->err = "词法错误";
}

static int parse_primary(parser_t* ps)
{
    lx_t* l = &ps->lx;
    int idx;

    if (ps->err) return -1;
    if (ps->depth++ > EX_DEPTH_MAX) {
        ps->err = "表达式嵌套超过 32 层";
        return -1;
    }

    if (l->type == T_NUM) {
        idx = new_node(ps, N_NUM);
        if (idx >= 0) ps->pool[idx].num = l->num;
        expect(ps);
        ps->depth--;
        return idx;
    }
    if (l->type == T_STR) {
        int len = (int)strlen(l->str);

        if (ps->arena_len + len + 1 > EX_ARENA_MAX) {
            ps->err = "字符串字面量总量超过上限";
            return -1;
        }
        idx = new_node(ps, N_STR);
        if (idx >= 0) {
            memcpy(ps->arena + ps->arena_len, l->str, (size_t)len + 1);
            ps->pool[idx].str_off = ps->arena_len;
            ps->arena_len += len + 1;
        }
        expect(ps);
        ps->depth--;
        return idx;
    }
    if (l->type == T_IDENT) {
        char name[EX_STR_MAX];

        snprintf(name, sizeof(name), "%s", l->str);
        expect(ps);

        if (strcmp(name, "true") == 0 || strcmp(name, "false") == 0) {
            idx = new_node(ps, N_BOOL);
            if (idx >= 0) ps->pool[idx].num = (name[0] == 't') ? 1 : 0;
            ps->depth--;
            return idx;
        }
        if (strcmp(name, "tag") == 0) {
            if (l->type != T_OP || l->op != O_LP) {
                ps->err = "tag 后面要跟 (";
                return -1;
            }
            expect(ps);
            if (l->type != T_NUM || l->num < 0 || l->num > 65535) {
                ps->err = "tag(<id>) 的 id 必须是 0..65535 的常量";
                return -1;
            }
            idx = new_node(ps, N_TAG);
            if (idx >= 0) {
                int id = (int)l->num;

                ps->pool[idx].num = l->num;
                if (ps->tag_count < EX_TAG_MAX) {
                    int dup = 0;

                    for (int i = 0; i < ps->tag_count; i++)
                        if (ps->tags[i] == id) dup = 1;
                    if (!dup) ps->tags[ps->tag_count++] = id;
                }
            }
            expect(ps);
            if (l->type != T_OP || l->op != O_RP) {
                ps->err = "tag(...) 缺右括号";
                return -1;
            }
            expect(ps);
            ps->depth--;
            return idx;
        }
        /* 函数调用 */
        {
            int fn = -1, argc = 0, args[3] = { -1, -1, -1 };

            for (size_t i = 0; i < sizeof(s_funcs) / sizeof(s_funcs[0]); i++)
                if (strcmp(s_funcs[i].name, name) == 0) { fn = (int)i; break; }
            if (fn < 0) {
                ps->err = "未知函数";
                return -1;
            }
            if (l->type != T_OP || l->op != O_LP) {
                ps->err = "函数名后面要跟 (";
                return -1;
            }
            expect(ps);
            if (l->type == T_OP && l->op == O_RP) {
                expect(ps);
            } else {
                for (;;) {
                    int a = parse_expr(ps);

                    if (a < 0) return -1;
                    if (argc >= 3) {
                        ps->err = "函数参数超过 3 个";
                        return -1;
                    }
                    args[argc++] = a;
                    if (l->type == T_OP && l->op == O_COMMA) { expect(ps); continue; }
                    break;
                }
                if (l->type != T_OP || l->op != O_RP) {
                    ps->err = "函数调用缺右括号";
                    return -1;
                }
                expect(ps);
            }
            if (s_funcs[fn].argc >= 0 && argc != s_funcs[fn].argc) {
                ps->err = "函数参数个数不对";
                return -1;
            }
            if (s_funcs[fn].argc < 0 && (argc < 2 || argc > 3)) {
                ps->err = "min/max 需要 2..3 个参数";
                return -1;
            }
            idx = new_node(ps, N_CALL);
            if (idx >= 0) {
                ps->pool[idx].fn = fn;
                ps->pool[idx].a = args[0];
                ps->pool[idx].b = args[1];
                ps->pool[idx].c = args[2];
            }
            ps->depth--;
            return idx;
        }
    }
    if (l->type == T_OP && l->op == O_LP) {
        expect(ps);
        idx = parse_expr(ps);
        if (idx < 0) return -1;
        if (l->type != T_OP || l->op != O_RP) {
            ps->err = "括号不匹配";
            return -1;
        }
        expect(ps);
        ps->depth--;
        return idx;
    }

    ps->err = "表达式语法错误";
    return -1;
}

static int parse_unary(parser_t* ps)
{
    lx_t* l = &ps->lx;

    if (l->type == T_OP && (l->op == O_NOT || l->op == O_SUB || l->op == O_BNOT)) {
        int op = (l->op == O_NOT) ? N_NOT : (l->op == O_SUB) ? N_NEG : N_BNOT;
        int idx;

        expect(ps);
        idx = parse_unary(ps);
        if (idx < 0) return -1;
        {
            int n = new_node(ps, op);

            if (n < 0) { ps->err = "表达式节点超过 256"; return -1; }
            ps->pool[n].a = idx;
            return n;
        }
    }
    return parse_primary(ps);
}

/* 二元运算：把左边作为 a、右边作为 b */
static int binary(parser_t* ps, int op, int lhs, int rhs)
{
    int n = new_node(ps, op);

    if (n < 0) { ps->err = "表达式节点超过 256"; return -1; }
    ps->pool[n].a = lhs;
    ps->pool[n].b = rhs;
    return n;
}

static int parse_mul(parser_t* ps)
{
    int lhs = parse_unary(ps);

    if (lhs < 0) return -1;
    while (ps->lx.type == T_OP &&
           (ps->lx.op == O_MUL || ps->lx.op == O_DIV || ps->lx.op == O_MOD)) {
        int op = (ps->lx.op == O_MUL) ? N_MUL : (ps->lx.op == O_DIV) ? N_DIV : N_MOD;
        int rhs;

        expect(ps);
        rhs = parse_unary(ps);
        if (rhs < 0) return -1;
        lhs = binary(ps, op, lhs, rhs);
        if (lhs < 0) return -1;
    }
    return lhs;
}

static int parse_add(parser_t* ps)
{
    int lhs = parse_mul(ps);

    if (lhs < 0) return -1;
    while (ps->lx.type == T_OP && (ps->lx.op == O_ADD || ps->lx.op == O_SUB)) {
        int op = (ps->lx.op == O_ADD) ? N_ADD : N_SUB;
        int rhs;

        expect(ps);
        rhs = parse_mul(ps);
        if (rhs < 0) return -1;
        lhs = binary(ps, op, lhs, rhs);
        if (lhs < 0) return -1;
    }
    return lhs;
}

static int parse_shift(parser_t* ps)
{
    int lhs = parse_add(ps);

    if (lhs < 0) return -1;
    while (ps->lx.type == T_OP && (ps->lx.op == O_SHL || ps->lx.op == O_SHR)) {
        int op = (ps->lx.op == O_SHL) ? N_SHL : N_SHR;
        int rhs;

        expect(ps);
        rhs = parse_add(ps);
        if (rhs < 0) return -1;
        lhs = binary(ps, op, lhs, rhs);
        if (lhs < 0) return -1;
    }
    return lhs;
}

static int parse_rel(parser_t* ps)
{
    int lhs = parse_shift(ps);

    if (lhs < 0) return -1;
    while (ps->lx.type == T_OP && (ps->lx.op == O_LT || ps->lx.op == O_LE ||
                                   ps->lx.op == O_GT || ps->lx.op == O_GE)) {
        int op = (ps->lx.op == O_LT) ? N_LT : (ps->lx.op == O_LE) ? N_LE :
                 (ps->lx.op == O_GT) ? N_GT : N_GE;
        int rhs;

        expect(ps);
        rhs = parse_shift(ps);
        if (rhs < 0) return -1;
        lhs = binary(ps, op, lhs, rhs);
        if (lhs < 0) return -1;
    }
    return lhs;
}

static int parse_eq(parser_t* ps)
{
    int lhs = parse_rel(ps);

    if (lhs < 0) return -1;
    while (ps->lx.type == T_OP && (ps->lx.op == O_EQ || ps->lx.op == O_NE)) {
        int op = (ps->lx.op == O_EQ) ? N_EQ : N_NE;
        int rhs;

        expect(ps);
        rhs = parse_rel(ps);
        if (rhs < 0) return -1;
        lhs = binary(ps, op, lhs, rhs);
        if (lhs < 0) return -1;
    }
    return lhs;
}

static int parse_band(parser_t* ps)
{
    int lhs = parse_eq(ps);

    if (lhs < 0) return -1;
    while (ps->lx.type == T_OP && ps->lx.op == O_BAND) {
        int rhs;

        expect(ps);
        rhs = parse_eq(ps);
        if (rhs < 0) return -1;
        lhs = binary(ps, N_BAND, lhs, rhs);
        if (lhs < 0) return -1;
    }
    return lhs;
}

static int parse_bxor(parser_t* ps)
{
    int lhs = parse_band(ps);

    if (lhs < 0) return -1;
    while (ps->lx.type == T_OP && ps->lx.op == O_BXOR) {
        int rhs;

        expect(ps);
        rhs = parse_band(ps);
        if (rhs < 0) return -1;
        lhs = binary(ps, N_BXOR, lhs, rhs);
        if (lhs < 0) return -1;
    }
    return lhs;
}

static int parse_bor(parser_t* ps)
{
    int lhs = parse_bxor(ps);

    if (lhs < 0) return -1;
    while (ps->lx.type == T_OP && ps->lx.op == O_BOR) {
        int rhs;

        expect(ps);
        rhs = parse_bxor(ps);
        if (rhs < 0) return -1;
        lhs = binary(ps, N_BOR, lhs, rhs);
        if (lhs < 0) return -1;
    }
    return lhs;
}

static int parse_and(parser_t* ps)
{
    int lhs = parse_bor(ps);

    if (lhs < 0) return -1;
    while (ps->lx.type == T_OP && ps->lx.op == O_AND) {
        int rhs;

        expect(ps);
        rhs = parse_bor(ps);
        if (rhs < 0) return -1;
        lhs = binary(ps, N_AND, lhs, rhs);
        if (lhs < 0) return -1;
    }
    return lhs;
}

static int parse_or(parser_t* ps)
{
    int lhs = parse_and(ps);

    if (lhs < 0) return -1;
    while (ps->lx.type == T_OP && ps->lx.op == O_OR) {
        int rhs;

        expect(ps);
        rhs = parse_and(ps);
        if (rhs < 0) return -1;
        lhs = binary(ps, N_OR, lhs, rhs);
        if (lhs < 0) return -1;
    }
    return lhs;
}

/* 三元右结合：cond ? a : b */
static int parse_ternary(parser_t* ps)
{
    int cond = parse_or(ps);

    if (cond < 0) return -1;
    if (ps->lx.type == T_OP && ps->lx.op == O_QUESTION) {
        int a, b, n;

        expect(ps);
        a = parse_expr(ps);
        if (a < 0) return -1;
        if (ps->lx.type != T_OP || ps->lx.op != O_COLON) {
            ps->err = "三元表达式缺冒号";
            return -1;
        }
        expect(ps);
        b = parse_ternary(ps);
        if (b < 0) return -1;
        n = new_node(ps, N_TERN);
        if (n < 0) { ps->err = "表达式节点超过 256"; return -1; }
        ps->pool[n].a = cond;
        ps->pool[n].b = a;
        ps->pool[n].c = b;
        return n;
    }
    return cond;
}

static int parse_expr(parser_t* ps)
{
    return parse_ternary(ps);
}

int expr_compile(const char* src, expr_prog_t* out, char* err, size_t errsz)
{
    parser_t* ps;
    int root;

    memset(out, 0, sizeof(*out));
    if (!src || !*src) {
        snprintf(err, errsz, "expr 为空");
        return -1;
    }
    ps = calloc(1, sizeof(*ps));
    if (!ps) {
        snprintf(err, errsz, "expr 编译缓冲区分配失败");
        return -1;
    }
    ps->lx.p = src;
    if (lex_next(&ps->lx) != 0) {
        snprintf(err, errsz, "expr 词法错误：%s", src);
        free(ps);
        return -1;
    }
    root = parse_expr(ps);
    if (root < 0 || ps->err) {
        snprintf(err, errsz, "expr 语法错误（%s）：%s", ps->err ? ps->err : "无法解析", src);
        free(ps);
        return -1;
    }
    if (ps->lx.type != T_END) {
        snprintf(err, errsz, "expr 末尾有多余内容：%s", src);
        free(ps);
        return -1;
    }

    out->nodes = malloc(sizeof(ex_node_t) * (size_t)ps->count);
    out->arena = malloc((size_t)ps->arena_len + 1);
    if (!out->nodes || !out->arena) {
        free(out->nodes);
        free(out->arena);
        memset(out, 0, sizeof(*out));
        snprintf(err, errsz, "expr 分配失败（%d 个节点）", ps->count);
        free(ps);
        return -1;
    }
    memcpy(out->nodes, ps->pool, sizeof(ex_node_t) * (size_t)ps->count);
    out->count = ps->count;
    memcpy(out->arena, ps->arena, (size_t)ps->arena_len);
    out->arena_len = ps->arena_len;
    out->tag_count = ps->tag_count;
    for (int i = 0; i < ps->tag_count; i++) out->tags[i] = ps->tags[i];
    /* 根节点放在最后：求值从 count-1 开始，子节点下标都小于它 */
    if (root != ps->count - 1) {
        ex_node_t tmp = out->nodes[root];

        out->nodes[root] = out->nodes[ps->count - 1];
        out->nodes[ps->count - 1] = tmp;
    }
    free(ps);
    return 0;
}

void expr_free(expr_prog_t* p)
{
    if (!p) return;
    free(p->nodes);
    free(p->arena);
    memset(p, 0, sizeof(*p));
}

/* ── 求值 ── */

/* tostring/substr 的结果要有地方放；用一个小环形缓冲，够一次求值里的临时字符串 */
static char s_strbuf[8][EX_STR_MAX];
static int  s_sbi;

static char* strbuf_take(void)
{
    char* p = s_strbuf[s_sbi];

    s_sbi = (s_sbi + 1) % 8;
    p[0] = '\0';
    return p;
}

static int as_num(const ex_val_t* v, double* out)
{
    if (v->type == EX_V_STR) return -1;      /* 不做隐式字符串转换 */
    *out = (v->type == EX_V_BOOL) ? (v->n != 0 ? 1.0 : 0.0) : v->n;
    return 0;
}

static int as_bool(const ex_val_t* v, int* out)
{
    double d;

    if (v->type == EX_V_STR) { *out = (v->s && v->s[0]) ? 1 : 0; return 0; }
    if (as_num(v, &d) != 0) return -1;
    *out = (d != 0.0);
    return 0;
}

static int ev(const expr_prog_t* p, int idx, ex_val_t* out, int depth);

static int ev_cmp(const expr_prog_t* p, const ex_node_t* n, ex_val_t* out, int depth)
{
    ex_val_t a, b;

    if (ev(p, n->a, &a, depth + 1) != 0) return -1;
    if (ev(p, n->b, &b, depth + 1) != 0) return -1;
    if (a.type == EX_V_STR || b.type == EX_V_STR) {
        int c;

        if (a.type != EX_V_STR || b.type != EX_V_STR) return -1;  /* 类型不一致 */
        c = strcmp(a.s ? a.s : "", b.s ? b.s : "");
        switch (n->op) {
        case N_EQ: out->n = (c == 0); break;
        case N_NE: out->n = (c != 0); break;
        case N_LT: out->n = (c < 0); break;
        case N_LE: out->n = (c <= 0); break;
        case N_GT: out->n = (c > 0); break;
        default:   out->n = (c >= 0); break;
        }
    } else {
        double x, y;

        if (as_num(&a, &x) != 0 || as_num(&b, &y) != 0) return -1;
        switch (n->op) {
        case N_EQ: out->n = (x == y); break;
        case N_NE: out->n = (x != y); break;
        case N_LT: out->n = (x < y); break;
        case N_LE: out->n = (x <= y); break;
        case N_GT: out->n = (x > y); break;
        default:   out->n = (x >= y); break;
        }
    }
    out->type = EX_V_BOOL;
    return 0;
}

static int ev_call(const expr_prog_t* p, const ex_node_t* n, ex_val_t* out, int depth)
{
    ex_val_t a[3];
    double d[3] = { 0, 0, 0 };
    int na = 0;

    for (int i = 0; i < 3; i++) {
        int ci = (i == 0) ? n->a : (i == 1) ? n->b : n->c;

        if (ci < 0) break;
        if (ev(p, ci, &a[na], depth + 1) != 0) return -1;
        if (a[na].type == EX_V_STR && n->fn != F_STRLEN && n->fn != F_SUBSTR &&
            n->fn != F_TOSTRING && n->fn != F_TOFLOAT && n->fn != F_TOINT)
            return -1;
        na++;
    }

    out->type = EX_V_NUM;
    switch (n->fn) {
    case F_ABS:
        if (as_num(&a[0], &d[0]) != 0) return -1;
        out->n = fabs(d[0]);
        return 0;
    case F_ROUND:
        if (as_num(&a[0], &d[0]) != 0) return -1;
        out->n = round(d[0]);
        return 0;
    case F_FLOOR:
        if (as_num(&a[0], &d[0]) != 0) return -1;
        out->n = floor(d[0]);
        return 0;
    case F_CEIL:
        if (as_num(&a[0], &d[0]) != 0) return -1;
        out->n = ceil(d[0]);
        return 0;
    case F_SQRT:
        if (as_num(&a[0], &d[0]) != 0 || d[0] < 0) return -1;
        out->n = sqrt(d[0]);
        return 0;
    case F_POW:
        if (as_num(&a[0], &d[0]) != 0 || as_num(&a[1], &d[1]) != 0) return -1;
        out->n = pow(d[0], d[1]);
        return 0;
    case F_SIGN:
        if (as_num(&a[0], &d[0]) != 0) return -1;
        out->n = (d[0] > 0) ? 1 : (d[0] < 0) ? -1 : 0;
        return 0;
    case F_TOINT:
        if (a[0].type == EX_V_STR) { out->n = strtod(a[0].s, NULL); return 0; }
        if (as_num(&a[0], &d[0]) != 0) return -1;
        out->n = (double)(long long)d[0];
        return 0;
    case F_TOFLOAT:
        if (a[0].type == EX_V_STR) { out->n = strtod(a[0].s, NULL); return 0; }
        if (as_num(&a[0], &d[0]) != 0) return -1;
        out->n = d[0];
        return 0;
    case F_MIN:
    case F_MAX:
    case F_CLAMP: {
        int cnt = (n->fn == F_CLAMP) ? 3 : na;

        for (int i = 0; i < cnt; i++)
            if (as_num(&a[i], &d[i]) != 0) return -1;
        if (n->fn == F_MIN) {
            double m = d[0];

            for (int i = 1; i < cnt; i++) if (d[i] < m) m = d[i];
            out->n = m;
        } else if (n->fn == F_MAX) {
            double m = d[0];

            for (int i = 1; i < cnt; i++) if (d[i] > m) m = d[i];
            out->n = m;
        } else {
            double lo = d[1], hi = d[2];

            if (lo > hi) { double t = lo; lo = hi; hi = t; }
            out->n = (d[0] < lo) ? lo : (d[0] > hi) ? hi : d[0];
        }
        return 0;
    }
    case F_TOSTRING: {
        char* buf = strbuf_take();

        if (a[0].type == EX_V_STR) {
            snprintf(buf, EX_STR_MAX, "%s", a[0].s ? a[0].s : "");
        } else {
            double x;

            if (as_num(&a[0], &x) != 0) return -1;
            if (x == (double)(long long)x) snprintf(buf, EX_STR_MAX, "%lld", (long long)x);
            else snprintf(buf, EX_STR_MAX, "%g", x);
        }
        out->type = EX_V_STR;
        out->s = buf;
        return 0;
    }
    case F_STRLEN:
        if (a[0].type == EX_V_STR) {
            out->n = (double)strlen(a[0].s ? a[0].s : "");
        } else {
            if (as_num(&a[0], &d[0]) != 0) return -1;
            out->n = 0;                 /* 非字符串：按规范不做隐式转换 */
        }
        return 0;
    case F_SUBSTR: {
        const char* s = (a[0].type == EX_V_STR) ? (a[0].s ? a[0].s : "") : "";
        double st, len;
        char* buf = strbuf_take();
        int slen = (int)strlen(s);

        if (as_num(&a[1], &st) != 0 || as_num(&a[2], &len) != 0) return -1;
        if (st < 0) st = 0;
        if (len < 0) len = 0;
        if ((int)st > slen) st = slen;
        if ((int)st + (int)len > slen) len = (double)(slen - (int)st);
        snprintf(buf, EX_STR_MAX, "%.*s", (int)len, s + (int)st);
        out->type = EX_V_STR;
        out->s = buf;
        return 0;
    }
    default:
        return -1;
    }
}

static int ev(const expr_prog_t* p, int idx, ex_val_t* out, int depth)
{
    const ex_node_t* n;
    ex_val_t a, b;

    if (depth > EX_DEPTH_MAX) return -1;
    if (idx < 0 || idx >= p->count) return -1;
    n = &p->nodes[idx];
    out->type = EX_V_NUM;
    out->n = 0;
    out->s = NULL;

    switch (n->op) {
    case N_NUM:
        out->n = n->num;
        return 0;
    case N_BOOL:
        out->type = EX_V_BOOL;
        out->n = n->num;
        return 0;
    case N_STR:
        out->type = EX_V_STR;
        out->s = p->arena + n->str_off;
        return 0;
    case N_TAG: {
        int id = (int)n->num;

        if (!tagdb_at(id)) return -1;
        out->n = tagdb_num(id);
        return 0;
    }
    case N_NEG:
        if (ev(p, n->a, &a, depth + 1) != 0) return -1;
        if (as_num(&a, &out->n) != 0) return -1;
        out->n = -out->n;
        return 0;
    case N_NOT:
        if (ev(p, n->a, &a, depth + 1) != 0) return -1;
        {
            int bo;

            if (as_bool(&a, &bo) != 0) return -1;
            out->type = EX_V_BOOL;
            out->n = bo ? 0 : 1;
        }
        return 0;
    case N_BNOT:
        if (ev(p, n->a, &a, depth + 1) != 0) return -1;
        if (as_num(&a, &out->n) != 0) return -1;
        out->n = (double)(~(long long)out->n);
        return 0;
    case N_AND:
    case N_OR: {
        int bo;

        if (ev(p, n->a, &a, depth + 1) != 0) return -1;
        if (as_bool(&a, &bo) != 0) return -1;
        if (n->op == N_AND && !bo) { out->type = EX_V_BOOL; out->n = 0; return 0; }
        if (n->op == N_OR && bo) { out->type = EX_V_BOOL; out->n = 1; return 0; }
        if (ev(p, n->b, &b, depth + 1) != 0) return -1;
        if (as_bool(&b, &bo) != 0) return -1;
        out->type = EX_V_BOOL;
        out->n = bo;
        return 0;
    }
    case N_TERN: {
        int bo;

        if (ev(p, n->a, &a, depth + 1) != 0) return -1;
        if (as_bool(&a, &bo) != 0) return -1;
        return ev(p, bo ? n->b : n->c, out, depth + 1);
    }
    case N_CALL:
        return ev_call(p, n, out, depth);
    case N_LT: case N_LE: case N_GT: case N_GE: case N_EQ: case N_NE:
        return ev_cmp(p, n, out, depth);
    default:
        break;
    }

    /* 其余都是数值二元运算 */
    if (ev(p, n->a, &a, depth + 1) != 0) return -1;
    if (ev(p, n->b, &b, depth + 1) != 0) return -1;
    {
        double x, y;

        if (as_num(&a, &x) != 0 || as_num(&b, &y) != 0) return -1;
        switch (n->op) {
        case N_ADD: out->n = x + y; return 0;
        case N_SUB: out->n = x - y; return 0;
        case N_MUL: out->n = x * y; return 0;
        case N_DIV:
            if (y == 0) return -1;          /* 除零：按规范保留上次的值 */
            out->n = x / y;
            return 0;
        case N_MOD:
            if (y == 0) return -1;
            out->n = fmod(x, y);
            return 0;
        case N_SHL: out->n = (double)((long long)x << (int)y); return 0;
        case N_SHR: out->n = (double)((long long)x >> (int)y); return 0;
        case N_BAND: out->n = (double)((long long)x & (long long)y); return 0;
        case N_BXOR: out->n = (double)((long long)x ^ (long long)y); return 0;
        case N_BOR: out->n = (double)((long long)x | (long long)y); return 0;
        default: return -1;
        }
    }
}

int expr_eval(const expr_prog_t* p, ex_val_t* out)
{
    if (!p || !p->nodes || p->count <= 0) return -1;
    return ev(p, p->count - 1, out, 0);
}
