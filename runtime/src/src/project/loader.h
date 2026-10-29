/*
 * loader.h — 组态工程加载：manifest/tags/devices/ports/pages 解析 + 校验
 *
 * 契约来自 37（v0.6）：常驻文件（manifest/tags/devices/ports）加载时全量校验，
 * 出错拒绝整个工程；页面文件进入时才解析（page_mgr 负责）。
 *
 * 失败时 err 写"文件 + JSON 路径 + 原因"——这是运行时错误页唯一显示的一行，
 * 也是串口日志里定位问题的依据。
 */
#ifndef HMI_LOADER_H
#define HMI_LOADER_H

#include "core/tagdb.h"     /* tag_type_t */
#include "hmi_format.h"     /* HMI_FORMAT_MAJOR/MINOR */
#include "hmi_paths.h"      /* HMI_PROJECTS_DIR */

#include <stddef.h>

/* 格式级枚举（37 §4/§5），与具体通讯驱动无关 */
enum {
    AREA_COIL = 0, AREA_DISCRETE, AREA_INPUT, AREA_HOLDING,
    AREA_DB, AREA_MK, AREA_PE, AREA_PA,
    AREA__COUNT
};

enum {
    DT_BOOL = 0, DT_I16, DT_U16, DT_I32, DT_U32, DT_F32, DT_I64, DT_DOUBLE, DT_STRING,
    DT__COUNT
};

/* 32/64 位数据的字节序与字序（37 §4 default_order）；v1 只实现前两种，
 * 其余取值在加载期直接报错，不静默按 ABCD 解。 */
enum {
    ORDER_ABCD = 0, ORDER_CDAB, ORDER_BADC, ORDER_DCBA,
    ORDER__COUNT
};

#define PROJ_TAGS_MAX   2000    /* 37 §5.11 */
#define PROJ_DEV_MAX      64
#define PROJ_PORTS_MAX     8
#define PROJ_FONT_MAX      8
#define PROJ_ERR_MAX     256

/* 工程根解析结果：无活动工程走提示页，其余失败走错误页 */
#define PROJECT_RESOLVE_OK    0
#define PROJECT_RESOLVE_NONE  1
#define PROJECT_RESOLVE_ERR  (-1)

/* tags.json 的可选 enum：值 → 文字。combobox 的 options_tag 用它生成选项；
 * 只有声明了 enum 的变量才分配，避免 2000 个变量都挂一张空表。 */
typedef struct {
    double v;
    char   text[20];
} tag_enum_t;

typedef struct {
    int id;
    char name[40];
    tag_type_t type;
    int access_rw;
    char unit[16];
    int has_min, has_max;
    double dmin, dmax;
    int has_source;
    char src_device[40];
    int  src_area;        /* AREA_* */
    int  src_addr;
    int  src_datatype;    /* DT_* */
    int  src_len;         /* string 类型的字节数（1..64），其它类型为 0 */
    int  src_dev_sim;     /* 1 = source 设备协议为 sim（v1 唯一可通讯协议） */
    tag_enum_t* enums;    /* 可选：声明了 enum 才分配 */
    int  enum_count;
} tag_def_t;

typedef struct {
    char id[40];
    char name[40];
    char protocol[16];
    int  poll_ms;         /* 37 §4：默认轮询周期，缺省 200 */

    /* modbus_rtu 专用（sim 设备这些字段保持缺省值） */
    char port[24];        /* ports.json 的逻辑串口名，如 com1 */
    int  slave;           /* 1..247，缺省 1 */
    int  timeout_ms;      /* 50..5000，缺省 300 */
    int  retries;         /* 0..5，缺省 2 */
    int  order;           /* ORDER_*，缺省 ABCD */
    int  gap;             /* 读块合并的地址间隙，0..64，缺省 8 */
} device_def_t;

typedef struct {
    char id[24];
    int  baud, data_bits, stop_bits;
    char parity[8];
} port_def_t;

typedef struct {
    char format_version[16];
    int  format_major, format_minor;
    char min_runtime[16];
    char hw_profile[32];
    char name[64];
    char version[32];
    int  screen_w, screen_h;
    char start_page[64];
    char default_language[8];
    /* 用户与权限（ADR-0012 / ADR-0026）：
     * 空闲超时分钟数（0 = 不超时）、上电自动登录的用户名，
     * 以及安全策略——连续失败次数/锁定时长（0 = 不锁）/会话总时长上限（0 = 不限）/
     * 口令最短长度。都夹在合法区间里，越界按边界取值。 */
    int  idle_min;
    char default_user[32];
    int  max_login_attempts;
    int  lockout_min;
    int  session_timeout_min;
    int  password_min_len;
    int  font_sizes[PROJ_FONT_MAX];
    int  font_size_count;
    /* manifest.features 的位图（位序见 caps.h 的 HMI_FEATURE_LIST）：
     * 装载期校验"声明了但固件没编"，建页期校验"用了却没声明"。 */
    unsigned features_mask;
    /* manifest.counts：编辑器声明的数量，仅用于交叉校验（37 §8 不一致是警告） */
    int  counts_tags, counts_devices, counts_pages, counts_scripts;
    int  has_counts;
} manifest_t;

typedef struct {
    char root[256];
    manifest_t manifest;
    tag_def_t* tags;
    int tags_count;
    device_def_t devices[PROJ_DEV_MAX];
    int devices_count;
    port_def_t ports[PROJ_PORTS_MAX];
    int ports_count;
} project_t;

/* 宿主调试用 HMI_PROJECT_DIR 覆盖工程根；否则按 active 指向的工程目录 */
int  project_resolve_root(char* root, size_t rootsz, char* err, size_t errsz);

/* 加载整个工程（manifest/ports/devices/tags + 起始页文件的存在性）。
 * 页面内容不在这里解析。成功返回 0，失败写 err 返回 -1。 */
int  project_load(const char* root, project_t* p, char* err, size_t errsz);
void project_free(project_t* p);

/* 把变量定义灌进 Tag DB（tagdb_init 之后调用；tag_t 字段由定义决定） */
void project_apply_tags(const project_t* p);

/* 页面文件路径：<root>/pages/<id>.json */
int  project_page_path(const project_t* p, const char* page_id,
                       char* out, size_t outsz);

/* 整文件读入内存（调用方 free；超 max_bytes 或空文件返回 NULL）。
 * page_mgr 读页面文件也用同一入口，避免两处文件 IO 各写一份。 */
char* loader_read_text(const char* path, size_t max_bytes, size_t* len_out);

/* 页面 id 合法性（小写字母/数字/下划线，≤63）：同时防路径穿越 */
int  page_id_valid(const char* id);

/* 按设备 id 找协议（找不到返回 NULL） */
const char* project_device_protocol(const project_t* p, const char* dev_id);

/* 按 id 取变量定义（找不到返回 NULL） */
const tag_def_t* project_tag(const project_t* p, int id);

/* 轮询周期：首个 sim 设备的 poll_ms，无 sim 设备或未声明时取 200（37 §4 缺省） */
int  project_poll_ms(const project_t* p);

#endif /* HMI_LOADER_H */
