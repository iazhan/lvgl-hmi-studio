/*
 * tagdb.h — 变量表（Tag DB）：稠密数组，下标即 tag id
 *
 * 对应 34 §5.5：加载时把变量重排成 0..N-1 连续，无需哈希表；只在值或质量
 * 真正变化时递增 version，UI 侧靠 version 比对决定是否重绘。
 *
 * 线程：所有读写入口过一把 pthread 互斥锁（UI 线程读、通讯线程写回）。ARM926EJ-S
 * 没有 LDREX/STREX，不写无锁结构（34 §5.4）。`tagdb_at()` 不加锁，只在通讯线程
 * 起来之前的启动阶段用（灌定义、绑定表构建）。
 */
#ifndef HMI_TAGDB_H
#define HMI_TAGDB_H

#include <stdint.h>

typedef enum {
    TAG_T_BOOL = 0,
    TAG_T_I16,
    TAG_T_U16,
    TAG_T_I32,
    TAG_T_U32,
    TAG_T_F32,
    TAG_T_I64,
    TAG_T_DOUBLE,
    TAG_T_STRING,
    TAG_T__COUNT
} tag_type_t;

enum {
    TAG_Q_GOOD = 0,     /* 值有效 */
    TAG_Q_UNINIT,       /* 尚未取过值 */
    TAG_Q_BAD           /* 通讯故障等 */
};

typedef struct {
    tag_type_t type;
    uint8_t    access_rw;   /* 1 = 可写（access: rw），0 = 只读（access: r） */
    uint8_t    quality;
    uint32_t   version;     /* 值或质量变化时递增 */
    double     num;         /* 数值型统一走 double；bool 用 0/1 */
    int64_t    i64;         /* i64/u64 专用：double 表示不了 64 位整数 */
    int has_min, has_max;
    double dmin, dmax;
    /* string 类型（ADR-0007）：定长原始字节，渲染期才解码 */
    uint8_t    slen;        /* 有效字节数（不含结尾 0） */
    uint8_t    scap;        /* 容量（字节，不含结尾 0）；0 = 不是 string 变量 */
    char*      sval;        /* 定长块，tagdb_string_bind 分配，分配 scap + 1 */
} tag_t;

/* 按工程变量数分配；count 为 0 时释放。返回 0 成功 */
int  tagdb_init(int count);
void tagdb_free(void);
int  tagdb_count(void);

/* 合法变量 id：工程变量（0..count-1）或系统变量（>= 60000，只读）。
 * 各处"tag 越界"判断统一走它，系统变量才能像普通变量一样被绑定/表达式引用。 */
int  tagdb_valid(int id);

/* 越界返回 NULL。不加锁：只允许在通讯线程起来之前用，运行期请走下面的 set/get */
tag_t* tagdb_at(int id);

/* 通讯侧写入：值或质量真正变化才递增 version */
void tagdb_set_num(int id, double v, int quality);
void tagdb_set_bool(int id, int v, int quality);
void tagdb_set_i64(int id, int64_t v, int quality);

/* UI 侧读取 */
double   tagdb_num(int id);
int      tagdb_bool(int id);
int64_t  tagdb_i64(int id);
uint32_t tagdb_version(int id);
int      tagdb_quality(int id);

/* 写入口（按钮动作 / 输入提交）：检查 access，量程外按 min/max 截断并记警告。
 * 不可写返回 -1；成功更新值并递增 version，返回 0。 */
/* 字符串（ADR-0007）：加载期绑缓冲、通讯侧整块写入、UI 侧拷出。
 * 内容或质量真的变化才递增 version，与数值变量同一条刷新路径。 */
int  tagdb_string_bind(int id, int len);       /* 重复绑定直接返回 0 */
void tagdb_set_str(int id, const char* data, int n, int quality);
int  tagdb_str_copy(int id, char* out, int outsz, int* q);   /* 返回有效字节数 */

int  tagdb_write_num(int id, double v);
int  tagdb_write_bool(int id, int v);

#endif /* HMI_TAGDB_H */
