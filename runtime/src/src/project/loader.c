/*
 * loader.c — 工程加载实现
 *
 * 读取策略：常驻文件整体读入内存（有大小上限）→ hmi_json 解析 → 逐字段校验 →
 * 拷进 project_t → 释放 DOM。工程文件来自 U 盘，任何字段都按不可信处理。
 */
#include "loader.h"
#include "core/caps.h"
#include "core/port_map.h"
#include "core/vfs.h"
#include "hmi_json.h"
#include "hmi_log.h"
#include "hmi_board.h"
#include "hmi_project_import.h"   /* hmi_import_active_id */

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

/* 各文件大小上限：工程文件本该很小，超限即拒（37 §2 的图片另有资产通道） */
#define FILE_MAX_BYTES (256 * 1024)

/* ── 小工具 ── */

/* 整文件读入内存（调用方 free）。超过 max_bytes 或空文件返回 NULL。 */
char* loader_read_text(const char* path, size_t max_bytes, size_t* len_out)
{
    FILE* f;
    char* buf;
    long sz;
    size_t got;

    /* 内存表优先（预览：工程还没落盘，见 core/vfs.h）。
     * 表为空时这一步直接返回 NULL，进程版行为与开销都不变。 */
    {
        size_t vlen = 0;
        const void* v = vfs_get(path, &vlen);

        if (v) {
            if (vlen == 0 || vlen > max_bytes) return NULL;
            buf = (char*)malloc(vlen + 1);
            if (!buf) return NULL;
            memcpy(buf, v, vlen);
            buf[vlen] = '\0';
            if (len_out) *len_out = vlen;
            return buf;
        }
    }

    f = fopen(path, "rb");
    if (!f) return NULL;
    if (fseek(f, 0, SEEK_END) != 0 || (sz = ftell(f)) <= 0 ||
        (size_t)sz > max_bytes) {
        fclose(f);
        return NULL;
    }
    rewind(f);
    buf = malloc((size_t)sz + 1);
    if (!buf) { fclose(f); return NULL; }
    got = fread(buf, 1, (size_t)sz, f);
    fclose(f);
    if (got != (size_t)sz) { free(buf); return NULL; }
    buf[sz] = '\0';
    if (len_out) *len_out = (size_t)sz;
    return buf;
}

static char* read_text(const char* path, size_t* len_out)
{
    return loader_read_text(path, FILE_MAX_BYTES, len_out);
}

/* 统一错误格式：文件: JSON 路径: 原因（错误页与串口共用这一行） */
static void errf(char* err, size_t errsz, const char* file,
                 const char* path, const char* fmt, ...)
{
    va_list ap;
    char why[192];

    va_start(ap, fmt);
    vsnprintf(why, sizeof(why), fmt, ap);
    va_end(ap);
    if (path && *path)
        snprintf(err, errsz, "%s: %s: %s", file, path, why);
    else
        snprintf(err, errsz, "%s: %s", file, why);
}

static int file_exists(const char* p)
{
    struct stat st;

    /* 内存表里的文件也算"存在"：预览时工程还没落盘（见 core/vfs.h） */
    if (vfs_get(p, NULL)) return 1;
    return stat(p, &st) == 0;
}

/* 页面 id 只允许小写字母/数字/下划线（37 §2 文件名规则）。
 * 同时是防路径穿越的手段：id 直接拼进 pages/<id>.json，放行 ".." 就能读
 * 工程目录外的文件。 */
int page_id_valid(const char* id)
{
    size_t i;
    size_t n = id ? strlen(id) : 0;

    if (n == 0 || n > 63) return 0;
    for (i = 0; i < n; i++) {
        char c = id[i];
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
            return 0;
    }
    return 1;
}

static int tag_type_from_str(const char* s, tag_type_t* out)
{
    static const char* const names[] = {
        "bool", "i16", "u16", "i32", "u32", "f32", "i64", "double", "string"
    };
    for (int i = 0; i < TAG_T__COUNT; i++) {
        if (strcmp(s, names[i]) == 0) { *out = (tag_type_t)i; return 0; }
    }
    return -1;
}

static int datatype_from_str(const char* s, int* out)
{
    static const char* const names[] = {
        "bool", "i16", "u16", "i32", "u32", "f32", "i64", "double", "string"
    };
    for (int i = 0; i < DT__COUNT; i++) {
        if (strcmp(s, names[i]) == 0) { *out = i; return 0; }
    }
    return -1;
}

/* type 是工程值类型，datatype 是原始值类型。v1 无 scale/offset，工程值等于
 * 原始值：数值类型之间可以互配（i16 原始值按 f32 使用是合法的）；bool 与
 * string 不做隐式转换，必须两侧一致。 */
static int type_compatible(tag_type_t t, int dt)
{
    if (t == TAG_T_STRING || dt == DT_STRING)
        return t == TAG_T_STRING && dt == DT_STRING;
    if (t == TAG_T_BOOL || dt == DT_BOOL)
        return t == TAG_T_BOOL && dt == DT_BOOL;
    return 1;
}

static int area_from_str(const char* s, int* out)
{
    static const char* const names[] = {
        "coil", "discrete", "input", "holding", "db", "mk", "pe", "pa"
    };
    for (int i = 0; i < AREA__COUNT; i++) {
        if (strcmp(s, names[i]) == 0) { *out = i; return 0; }
    }
    return -1;
}

/* "主.次" → 两个整数；两段都必须是数字。成功返回 0 */
static int version_split(const char* s, int* major, int* minor)
{
    char* end = NULL;
    const char* p2;
    long mj, mn;

    if (!s || !*s) return -1;
    mj = strtol(s, &end, 10);
    if (end == s || *end != '.') return -1;
    p2 = end + 1;
    mn = strtol(p2, &end, 10);
    if (end == p2) return -1;
    *major = (int)mj;
    *minor = (int)mn;
    return 0;
}

/* "主.次.修订" 三段比较：need 高于 have 返回 1，相等 0，低于 -1。
 * 段数不足按 0 计（0.1 与 0.1.0 等价）；非数字段按 0 计。 */
static int version_cmp3(const char* a, const char* b)
{
    int av[3] = {0, 0, 0}, bv[3] = {0, 0, 0};
    int ai = 0, bi = 0;
    const char* p;

    for (p = a; *p && ai < 3; p++) {
        if (*p == '.') { ai++; continue; }
        if (*p >= '0' && *p <= '9')
            av[ai] = av[ai] * 10 + (*p - '0');
    }
    for (p = b; *p && bi < 3; p++) {
        if (*p == '.') { bi++; continue; }
        if (*p >= '0' && *p <= '9')
            bv[bi] = bv[bi] * 10 + (*p - '0');
    }
    for (int i = 0; i < 3; i++) {
        if (av[i] != bv[i]) return av[i] > bv[i] ? 1 : -1;
    }
    return 0;
}

const char* project_device_protocol(const project_t* p, const char* dev_id)
{
    for (int i = 0; i < p->devices_count; i++)
        if (strcmp(p->devices[i].id, dev_id) == 0) return p->devices[i].protocol;
    return NULL;
}

/* ── manifest ── */

static int load_manifest(const char* root, project_t* p, char* err, size_t errsz)
{
    char path[300];
    char jerr[160];
    size_t len = 0;
    char* text;
    hmi_json_t* m;
    const hmi_json_t* v;
    const char* s;

    if (snprintf(path, sizeof(path), "%s/manifest.json", root) >= (int)sizeof(path)) {
        errf(err, errsz, "manifest.json", NULL, "工程根路径过长");
        return -1;
    }
    text = read_text(path, &len);
    if (!text) {
        errf(err, errsz, "manifest.json", NULL, "缺失或超过 %dKB", FILE_MAX_BYTES / 1024);
        return -1;
    }
    m = hmi_json_parse(text, len, jerr, sizeof(jerr));
    free(text);
    if (!m) {
        errf(err, errsz, "manifest.json", NULL, "解析失败：%s", jerr);
        return -1;
    }
    if (hmi_json_type(m) != HMI_JSON_OBJ) {
        errf(err, errsz, "manifest.json", NULL, "根节点不是对象");
        hmi_json_free(m);
        return -1;
    }

    /* format_version：主版本不同拒绝，次版本高于支持值拒绝（37 §9） */
    s = hmi_json_str(hmi_json_get(m, "format_version"), NULL);
    if (!s || version_split(s, &p->manifest.format_major,
                            &p->manifest.format_minor) != 0) {
        errf(err, errsz, "manifest.json", "format_version", "缺失或格式非法");
        hmi_json_free(m);
        return -1;
    }
    snprintf(p->manifest.format_version, sizeof(p->manifest.format_version),
             "%s", s);
    if (p->manifest.format_major != HMI_FORMAT_MAJOR ||
        p->manifest.format_minor > HMI_FORMAT_MINOR) {
        errf(err, errsz, "manifest.json", "format_version",
             "工程格式 %s 不被支持（运行时支持 %d.%d）", s,
             HMI_FORMAT_MAJOR, HMI_FORMAT_MINOR);
        hmi_json_free(m);
        return -1;
    }

    /* min_runtime 不高于运行时版本（37 §9） */
    s = hmi_json_str(hmi_json_get(m, "min_runtime"), NULL);
    if (!s || !*s) {
        errf(err, errsz, "manifest.json", "min_runtime", "缺失");
        hmi_json_free(m);
        return -1;
    }
    snprintf(p->manifest.min_runtime, sizeof(p->manifest.min_runtime), "%s", s);
    if (version_cmp3(s, APP_VERSION) > 0) {
        errf(err, errsz, "manifest.json", "min_runtime",
             "要求运行时 %s，当前 %s，请升级固件", s, APP_VERSION);
        hmi_json_free(m);
        return -1;
    }

    /* 屏幕与硬件配置：与运行时不一致拒绝加载（37 §8） */
    v = hmi_json_get(m, "screen");
    p->manifest.screen_w = (int)hmi_json_int(hmi_json_get(v, "width"), -1);
    p->manifest.screen_h = (int)hmi_json_int(hmi_json_get(v, "height"), -1);
    if (p->manifest.screen_w != HMI_LCD_WIDTH || p->manifest.screen_h != HMI_LCD_HEIGHT) {
        errf(err, errsz, "manifest.json", "screen",
             "屏幕 %dx%d 与本机 %dx%d 不一致",
             p->manifest.screen_w, p->manifest.screen_h,
             HMI_LCD_WIDTH, HMI_LCD_HEIGHT);
        hmi_json_free(m);
        return -1;
    }
    s = hmi_json_str(hmi_json_get(m, "hw_profile"), NULL);
    if (!s || strcmp(s, HMI_BOARD_ID) != 0) {
        errf(err, errsz, "manifest.json", "hw_profile",
             "硬件配置 %s 与本机 %s 不一致", s ? s : "(缺失)", HMI_BOARD_ID);
        hmi_json_free(m);
        return -1;
    }
    snprintf(p->manifest.hw_profile, sizeof(p->manifest.hw_profile), "%s", s);

    /* name/version/start_page/default_language */
    s = hmi_json_str(hmi_json_get(m, "name"), NULL);
    if (!s || !*s || strlen(s) >= sizeof(p->manifest.name)) {
        errf(err, errsz, "manifest.json", "name", "缺失或过长");
        hmi_json_free(m);
        return -1;
    }
    snprintf(p->manifest.name, sizeof(p->manifest.name), "%s", s);
    s = hmi_json_str(hmi_json_get(m, "version"), "0.0.0");
    snprintf(p->manifest.version, sizeof(p->manifest.version), "%s", s);
    s = hmi_json_str(hmi_json_get(m, "start_page"), NULL);
    if (!s || !*s || strlen(s) >= sizeof(p->manifest.start_page) ||
        !page_id_valid(s)) {
        errf(err, errsz, "manifest.json", "start_page",
             "缺失、过长或含非法字符（只允许小写字母/数字/下划线）");
        hmi_json_free(m);
        return -1;
    }
    snprintf(p->manifest.start_page, sizeof(p->manifest.start_page), "%s", s);
    s = hmi_json_str(hmi_json_get(m, "default_language"), "zh");
    snprintf(p->manifest.default_language, sizeof(p->manifest.default_language), "%s", s);

    /* 用户与权限（ADR-0012 / ADR-0026）：idle_min 0..240，default_user 可空 */
    p->manifest.idle_min = (int)hmi_json_int(hmi_json_get(m, "idle_min"), 10);
    if (p->manifest.idle_min < 0) p->manifest.idle_min = 0;
    if (p->manifest.idle_min > 240) p->manifest.idle_min = 240;
    s = hmi_json_str(hmi_json_get(m, "default_user"), "");
    snprintf(p->manifest.default_user, sizeof(p->manifest.default_user), "%s",
             s ? s : "");
    /* 安全策略：缺省即老行为（3 次 / 锁 1 分钟 / 不限会话总时长 / 口令 4 位） */
    p->manifest.max_login_attempts =
        (int)hmi_json_int(hmi_json_get(m, "max_login_attempts"), 3);
    if (p->manifest.max_login_attempts < 1) p->manifest.max_login_attempts = 1;
    if (p->manifest.max_login_attempts > 10) p->manifest.max_login_attempts = 10;
    p->manifest.lockout_min = (int)hmi_json_int(hmi_json_get(m, "lockout_min"), 1);
    if (p->manifest.lockout_min < 0) p->manifest.lockout_min = 0;
    if (p->manifest.lockout_min > 60) p->manifest.lockout_min = 60;
    p->manifest.session_timeout_min =
        (int)hmi_json_int(hmi_json_get(m, "session_timeout_min"), 0);
    if (p->manifest.session_timeout_min < 0) p->manifest.session_timeout_min = 0;
    if (p->manifest.session_timeout_min > 1440) p->manifest.session_timeout_min = 1440;
    p->manifest.password_min_len = (int)hmi_json_int(hmi_json_get(m, "password_min_len"), 4);
    if (p->manifest.password_min_len < 4) p->manifest.password_min_len = 4;
    if (p->manifest.password_min_len > 16) p->manifest.password_min_len = 16;

    /* font_sizes：不超过 8 档（37 §5.11） */
    v = hmi_json_get(m, "font_sizes");
    if (hmi_json_type(v) != HMI_JSON_ARR) {
        errf(err, errsz, "manifest.json", "font_sizes", "缺失或不是数组");
        hmi_json_free(m);
        return -1;
    }
    p->manifest.font_size_count = (int)hmi_json_count(v);
    if (p->manifest.font_size_count > PROJ_FONT_MAX) {
        errf(err, errsz, "manifest.json", "font_sizes",
             "字号档位 %d 超过上限 %d", p->manifest.font_size_count, PROJ_FONT_MAX);
        hmi_json_free(m);
        return -1;
    }
    /* font_sizes 是「工程用到的字号档位」声明（37 §5），不是控件字号的白名单；
     * 只校验元素本身：正整数、不重复。 */
    for (int i = 0; i < p->manifest.font_size_count; i++) {
        long fs = hmi_json_int(hmi_json_at(v, (size_t)i), -1);
        char where[32];

        snprintf(where, sizeof(where), "font_sizes[%d]", i);
        if (fs < 8 || fs > 128) {
            errf(err, errsz, "manifest.json", where,
                 "字号 %ld 越界（8..128）", fs);
            hmi_json_free(m);
            return -1;
        }
        for (int j = 0; j < i; j++) {
            if (p->manifest.font_sizes[j] == (int)fs) {
                errf(err, errsz, "manifest.json", where, "字号 %ld 重复", fs);
                hmi_json_free(m);
                return -1;
            }
        }
        p->manifest.font_sizes[i] = (int)fs;
    }

    /* counts：编辑器声明的数量，与实际不一致只警告（37 §8） */
    v = hmi_json_get(m, "counts");
    if (hmi_json_type(v) == HMI_JSON_OBJ) {
        p->manifest.has_counts = 1;
        p->manifest.counts_tags =
            (int)hmi_json_int(hmi_json_get(v, "tags"), -1);
        p->manifest.counts_devices =
            (int)hmi_json_int(hmi_json_get(v, "devices"), -1);
        p->manifest.counts_pages =
            (int)hmi_json_int(hmi_json_get(v, "pages"), -1);
        p->manifest.counts_scripts =
            (int)hmi_json_int(hmi_json_get(v, "scripts"), -1);
    }

    /* features：能力声明（46 §1）。取值必须在 spec/features.json 的词汇表里；
     * 声明了本固件没编进来的能力就直接拒绝——不静默降级，否则现场表现为
     * "界面在、数据不动"。反向（用了却没声明）由各子系统的加载期检查负责。 */
    v = hmi_json_get(m, "features");
    if (hmi_json_type(v) == HMI_JSON_ARR) {
        size_t nf = hmi_json_count(v);

        for (size_t i = 0; i < nf; i++) {
            const char* f = hmi_json_str(hmi_json_at(v, i), "");
            char at[48];
            int ok = caps_feature(f);

            snprintf(at, sizeof(at), "features[%zu]", i);
            if (ok < 0) {
                errf(err, errsz, "manifest.json", at,
                     "未知能力 %s（取值见 spec/features.json）", f);
                hmi_json_free(m);
                return -1;
            }
            if (ok == 0) {
                const char* mod = caps_feature_module(f);
                errf(err, errsz, "manifest.json", at,
                     "本固件未包含能力 %s（模块 %s 未编入），请换固件或去掉该声明",
                     f, mod ? mod : "?");
                hmi_json_free(m);
                return -1;
            }
            {
                int idx = caps_feature_index(f);
                if (idx < 0 || idx >= HMI_FEATURE_BITS) {
                    errf(err, errsz, "manifest.json", at,
                         "能力 %s 的位序越界（能力表超过 %d 项，需扩 features_mask）",
                         f, HMI_FEATURE_BITS);
                    hmi_json_free(m);
                    return -1;
                }
                p->manifest.features_mask |= (1u << idx);
            }
        }
    }

    hmi_json_free(m);
    return 0;
}

/* ── ports ── */

static int load_ports(const char* root, project_t* p, char* err, size_t errsz)
{
    char path[300], jerr[160];
    size_t len = 0;
    char* text;
    hmi_json_t* a;
    size_t n, i;

    snprintf(path, sizeof(path), "%s/ports.json", root);
    text = read_text(path, &len);
    if (!text) {
        errf(err, errsz, "ports.json", NULL, "缺失或超过 %dKB", FILE_MAX_BYTES / 1024);
        return -1;
    }
    a = hmi_json_parse(text, len, jerr, sizeof(jerr));
    free(text);
    if (!a) {
        errf(err, errsz, "ports.json", NULL, "解析失败：%s", jerr);
        return -1;
    }
    if (hmi_json_type(a) != HMI_JSON_ARR) {
        errf(err, errsz, "ports.json", NULL, "根节点不是数组");
        hmi_json_free(a);
        return -1;
    }
    n = hmi_json_count(a);
    if (n > PROJ_PORTS_MAX) {
        errf(err, errsz, "ports.json", NULL, "串口数 %zu 超过上限 %d",
             n, PROJ_PORTS_MAX);
        hmi_json_free(a);
        return -1;
    }
    p->ports_count = (int)n;
    for (i = 0; i < n; i++) {
        const hmi_json_t* e = hmi_json_at(a, i);
        const char* id = hmi_json_str(hmi_json_get(e, "id"), NULL);
        if (!id || !*id) {
            char where[32];
            snprintf(where, sizeof(where), "[%zu].id", i);
            errf(err, errsz, "ports.json", where, "缺失");
            hmi_json_free(a);
            return -1;
        }
        /* 逻辑串口名必须是本板真的有的那个（ADR 0001：映射编译期定死） */
        if (!port_known(id)) {
            char where[32];
            snprintf(where, sizeof(where), "[%zu].id", i);
            errf(err, errsz, "ports.json", where,
                 "本板没有逻辑串口 %s（本板只有 com1）", id);
            hmi_json_free(a);
            return -1;
        }
        snprintf(p->ports[i].id, sizeof(p->ports[i].id), "%s", id);
        p->ports[i].baud = (int)hmi_json_int(hmi_json_get(e, "baud"), 9600);
        p->ports[i].data_bits = (int)hmi_json_int(hmi_json_get(e, "data_bits"), 8);
        p->ports[i].stop_bits = (int)hmi_json_int(hmi_json_get(e, "stop_bits"), 1);
        {
            const char* par = hmi_json_str(hmi_json_get(e, "parity"), "none");
            snprintf(p->ports[i].parity, sizeof(p->ports[i].parity), "%s", par);
        }
    }
    hmi_json_free(a);
    if (p->ports_count > 0)
        HMI_LOG("loader: 声明了 %d 个逻辑串口", p->ports_count);
    return 0;
}

/* ── devices ── */

/* modbus_rtu 的专属字段。port 必须是本板声明过、且工程 ports.json 里也声明了的
 * 逻辑串口——两条都要满足：前者防「板子没有这个口」，后者防「设备引用了未声明
 * 的串口参数」。 */
static int parse_modbus_dev(project_t* p, const hmi_json_t* e, size_t i,
                            char* err, size_t errsz)
{
    device_def_t* d = &p->devices[i];
    const char* port = hmi_json_str(hmi_json_get(e, "port"), NULL);
    const char* ord = hmi_json_str(hmi_json_get(e, "default_order"), "ABCD");
    char where[48];
    int found = 0;

    if (!port || !*port) {
        errf(err, errsz, "devices.json", "port",
             "设备 %s 是 modbus_rtu，必须给 port", d->id);
        return -1;
    }
    snprintf(where, sizeof(where), "[%zu].port", i);
    if (!port_known(port)) {
        errf(err, errsz, "devices.json", where,
             "本板没有逻辑串口 %s（本板只有 com1）", port);
        return -1;
    }
    for (int k = 0; k < p->ports_count; k++) {
        if (strcmp(p->ports[k].id, port) == 0) { found = 1; break; }
    }
    if (!found) {
        errf(err, errsz, "devices.json", where, "port %s 不在 ports.json 中", port);
        return -1;
    }
    snprintf(d->port, sizeof(d->port), "%s", port);

    d->slave = (int)hmi_json_int(hmi_json_get(e, "slave"), 1);
    if (d->slave < 1 || d->slave > 247) {
        snprintf(where, sizeof(where), "[%zu].slave", i);
        errf(err, errsz, "devices.json", where, "slave %d 越界（1..247）", d->slave);
        return -1;
    }
    d->timeout_ms = (int)hmi_json_int(hmi_json_get(e, "timeout_ms"), 300);
    if (d->timeout_ms < 50 || d->timeout_ms > 5000) {
        snprintf(where, sizeof(where), "[%zu].timeout_ms", i);
        errf(err, errsz, "devices.json", where,
             "timeout_ms %d 越界（50..5000）", d->timeout_ms);
        return -1;
    }
    d->retries = (int)hmi_json_int(hmi_json_get(e, "retries"), 2);
    if (d->retries < 0 || d->retries > 5) {
        snprintf(where, sizeof(where), "[%zu].retries", i);
        errf(err, errsz, "devices.json", where, "retries %d 越界（0..5）", d->retries);
        return -1;
    }
    d->gap = (int)hmi_json_int(hmi_json_get(e, "gap"), 8);
    if (d->gap < 0 || d->gap > 64) {
        snprintf(where, sizeof(where), "[%zu].gap", i);
        errf(err, errsz, "devices.json", where, "gap %d 越界（0..64）", d->gap);
        return -1;
    }

    /* default_order：已实现 ABCD / CDAB / BADC；DCBA 明确报错而不是静默按 ABCD 解。
     * BADC 是「每个 16 位寄存器内交换高低字节」，字符串变量（ADR-0007）常用它。 */
    if (!ord || !*ord) ord = "ABCD";
    if (strcmp(ord, "ABCD") == 0) {
        d->order = ORDER_ABCD;
    } else if (strcmp(ord, "CDAB") == 0) {
        d->order = ORDER_CDAB;
    } else if (strcmp(ord, "BADC") == 0) {
        d->order = ORDER_BADC;
    } else {
        snprintf(where, sizeof(where), "[%zu].default_order", i);
        errf(err, errsz, "devices.json", where,
             "order %s 暂未实现（支持 ABCD / CDAB / BADC）", ord);
        return -1;
    }
    return 0;
}

static int load_devices(const char* root, project_t* p, char* err, size_t errsz)
{
    char path[300], jerr[160];
    size_t len = 0;
    char* text;
    hmi_json_t* a;
    size_t n, i;

    snprintf(path, sizeof(path), "%s/devices.json", root);
    text = read_text(path, &len);
    if (!text) {
        errf(err, errsz, "devices.json", NULL, "缺失或超过 %dKB", FILE_MAX_BYTES / 1024);
        return -1;
    }
    a = hmi_json_parse(text, len, jerr, sizeof(jerr));
    free(text);
    if (!a) {
        errf(err, errsz, "devices.json", NULL, "解析失败：%s", jerr);
        return -1;
    }
    if (hmi_json_type(a) != HMI_JSON_ARR) {
        errf(err, errsz, "devices.json", NULL, "根节点不是数组");
        hmi_json_free(a);
        return -1;
    }
    n = hmi_json_count(a);
    if (n > PROJ_DEV_MAX) {
        errf(err, errsz, "devices.json", NULL, "设备数 %zu 超过上限 %d",
             n, PROJ_DEV_MAX);
        hmi_json_free(a);
        return -1;
    }
    p->devices_count = (int)n;
    for (i = 0; i < n; i++) {
        const hmi_json_t* e = hmi_json_at(a, i);
        const char* id = hmi_json_str(hmi_json_get(e, "id"), NULL);
        const char* proto = hmi_json_str(hmi_json_get(e, "protocol"), NULL);
        const char* name;
        char at[48];

        if (!id || !*id || strlen(id) >= sizeof(p->devices[i].id)) {
            snprintf(at, sizeof(at), "[%zu].id", i);
            errf(err, errsz, "devices.json", at, "缺失或过长");
            hmi_json_free(a);
            return -1;
        }
        if (!proto || !*proto) {
            errf(err, errsz, "devices.json", "protocol", "设备 %s 缺 protocol", id);
            hmi_json_free(a);
            return -1;
        }
        /* v1 支持 sim 与 modbus_rtu；其余明确拒绝，不静默降级。
         * sim 还要看固件有没有编 sim 模块（产品镜像通常会关掉假设备）。 */
        if (strcmp(proto, "sim") != 0 && strcmp(proto, "modbus_rtu") != 0) {
            snprintf(at, sizeof(at), "[%zu].protocol", i);
            errf(err, errsz, "devices.json", at,
                 "设备 %s 的协议 %s 暂未实现（v1 支持 sim / modbus_rtu）", id, proto);
            hmi_json_free(a);
            return -1;
        }
        if (strcmp(proto, "sim") == 0 && caps_module("sim") != 1) {
            snprintf(at, sizeof(at), "[%zu].protocol", i);
            errf(err, errsz, "devices.json", at,
                 "设备 %s 用 sim 协议，但本固件未编入 sim 模块", id);
            hmi_json_free(a);
            return -1;
        }
        for (int j = 0; j < (int)i; j++) {
            if (strcmp(p->devices[j].id, id) == 0) {
                snprintf(at, sizeof(at), "[%zu].id", i);
                errf(err, errsz, "devices.json", at, "设备 id %s 重复", id);
                hmi_json_free(a);
                return -1;
            }
        }
        snprintf(p->devices[i].id, sizeof(p->devices[i].id), "%s", id);
        snprintf(p->devices[i].protocol, sizeof(p->devices[i].protocol), "%s", proto);
        name = hmi_json_str(hmi_json_get(e, "name"), id);
        snprintf(p->devices[i].name, sizeof(p->devices[i].name), "%s", name);
        p->devices[i].poll_ms =
            (int)hmi_json_int(hmi_json_get(e, "poll_ms"), 200);
        if (p->devices[i].poll_ms < 50 || p->devices[i].poll_ms > 60000) {
            snprintf(at, sizeof(at), "[%zu].poll_ms", i);
            errf(err, errsz, "devices.json", at,
                 "poll_ms %d 越界（50..60000）", p->devices[i].poll_ms);
            hmi_json_free(a);
            return -1;
        }
        if (strcmp(proto, "modbus_rtu") == 0 &&
            parse_modbus_dev(p, e, i, err, errsz) != 0) {
            hmi_json_free(a);
            return -1;
        }
    }
    hmi_json_free(a);
    return 0;
}

/* ── tags ── */

static int load_tags(const char* root, project_t* p, char* err, size_t errsz)
{
    char path[300], jerr[160];
    size_t len = 0;
    char* text;
    hmi_json_t* a;
    size_t n, i;

    snprintf(path, sizeof(path), "%s/tags.json", root);
    text = read_text(path, &len);
    if (!text) {
        errf(err, errsz, "tags.json", NULL, "缺失或超过 %dKB", FILE_MAX_BYTES / 1024);
        return -1;
    }
    a = hmi_json_parse(text, len, jerr, sizeof(jerr));
    free(text);
    if (!a) {
        errf(err, errsz, "tags.json", NULL, "解析失败：%s", jerr);
        return -1;
    }
    if (hmi_json_type(a) != HMI_JSON_ARR) {
        errf(err, errsz, "tags.json", NULL, "根节点不是数组");
        hmi_json_free(a);
        return -1;
    }
    n = hmi_json_count(a);
    if (n > PROJ_TAGS_MAX) {
        errf(err, errsz, "tags.json", NULL, "变量数 %zu 超过上限 %d",
             n, PROJ_TAGS_MAX);
        hmi_json_free(a);
        return -1;
    }
    p->tags = calloc(n ? n : 1, sizeof(*p->tags));
    if (!p->tags) {
        errf(err, errsz, "tags.json", NULL, "内存不足");
        hmi_json_free(a);
        return -1;
    }
    p->tags_count = (int)n;

    for (i = 0; i < n; i++) {
        const hmi_json_t* e = hmi_json_at(a, i);
        tag_def_t* t = &p->tags[i];
        char where[48];
        const char* s;

        snprintf(where, sizeof(where), "[%zu]", i);

        /* id 从 0 连续且唯一（37 §8） */
        long id = hmi_json_int(hmi_json_get(e, "id"), -1);
        if (id != (long)i) {
            errf(err, errsz, "tags.json", where,
                 "id=%ld，要求从 0 连续（期望 %zu）", id, i);
            goto fail;
        }
        t->id = (int)id;

        s = hmi_json_str(hmi_json_get(e, "name"), NULL);
        if (!s || !*s) {
            errf(err, errsz, "tags.json", where, "name 缺失");
            goto fail;
        }
        if (strlen(s) >= sizeof(t->name)) {
            errf(err, errsz, "tags.json", where, "name 超过 %zu 字节",
                 sizeof(t->name) - 1);
            goto fail;
        }
        snprintf(t->name, sizeof(t->name), "%s", s);

        s = hmi_json_str(hmi_json_get(e, "type"), NULL);
        if (!s || tag_type_from_str(s, &t->type) != 0) {
            errf(err, errsz, "tags.json", where, "type %s 非法或暂未支持", s ? s : "(缺失)");
            goto fail;
        }
        /* string（ADR-0007）：定长原始字节，长度由 len 指定，缺省 16 字节 */
        if (t->type == TAG_T_STRING) {
            long len = hmi_json_int(hmi_json_get(e, "len"), 16);

            if (len < 1 || len > 64) {
                errf(err, errsz, "tags.json", where,
                     "string 的 len %ld 越界（1..64 字节）", len);
                goto fail;
            }
            t->src_len = (int)len;
        }

        s = hmi_json_str(hmi_json_get(e, "access"), "r");
        if (strcmp(s, "r") == 0) t->access_rw = 0;
        else if (strcmp(s, "rw") == 0) t->access_rw = 1;
        else {
            errf(err, errsz, "tags.json", where, "access %s 非法（r/rw）", s);
            goto fail;
        }

        s = hmi_json_str(hmi_json_get(e, "unit"), "");
        if (*s) snprintf(t->unit, sizeof(t->unit), "%s", s);

        {
            const hmi_json_t* mn = hmi_json_get(e, "min");
            const hmi_json_t* mx = hmi_json_get(e, "max");
            if (hmi_json_type(mn) == HMI_JSON_NUM) {
                t->has_min = 1;
                t->dmin = hmi_json_num(mn, 0);
            }
            if (hmi_json_type(mx) == HMI_JSON_NUM) {
                t->has_max = 1;
                t->dmax = hmi_json_num(mx, 0);
            }
        }

        /* source：没有的是内部变量；v1 内部变量按初值 0 工作 */
        {
            const hmi_json_t* src = hmi_json_get(e, "source");
            if (hmi_json_type(src) == HMI_JSON_OBJ) {
                const char* dev = hmi_json_str(hmi_json_get(src, "device"), NULL);
                const char* area = hmi_json_str(hmi_json_get(src, "area"), NULL);
                const char* dt = hmi_json_str(hmi_json_get(src, "datatype"), NULL);

                if (!dev || !project_device_protocol(p, dev)) {
                    errf(err, errsz, "tags.json", where,
                         "source.device %s 不在 devices.json 中", dev ? dev : "(缺失)");
                    goto fail;
                }
                if (!area || area_from_str(area, &t->src_area) != 0) {
                    errf(err, errsz, "tags.json", where,
                         "source.area %s 非法", area ? area : "(缺失)");
                    goto fail;
                }
                if (!dt || datatype_from_str(dt, &t->src_datatype) != 0) {
                    errf(err, errsz, "tags.json", where,
                         "source.datatype %s 非法", dt ? dt : "(缺失)");
                    goto fail;
                }
                t->src_addr = (int)hmi_json_int(hmi_json_get(src, "addr"), -1);
                if (t->src_addr < 0 || t->src_addr > 65535) {
                    errf(err, errsz, "tags.json", where,
                         "source.addr %d 越界（0..65535）", t->src_addr);
                    goto fail;
                }
                snprintf(t->src_device, sizeof(t->src_device), "%s", dev);

                /* Modbus 主站 v1 只解到 32 位：64 位与字符串在加载期拒掉，
                 * 免得运行期默默读错数。sim 不受影响（它自己那套内存模型）。 */
                if ((t->src_datatype == DT_I64 || t->src_datatype == DT_DOUBLE) &&
                    strcmp(project_device_protocol(p, dev), "sim") != 0) {
                    errf(err, errsz, "tags.json", where,
                         "source.datatype %s 暂不支持（Modbus v1 只到 32 位）", dt);
                    goto fail;
                }
                /* string 必须带 len：寄存器数 = ceil(len/2)，没 len 就算不出读多长 */
                if (t->src_datatype == DT_STRING && t->src_len <= 0) {
                    errf(err, errsz, "tags.json", where,
                         "datatype=string 需要同时给 len（字节数）");
                    goto fail;
                }
                /* 可选 enum：值 → 文字，供 combobox 的 options_tag 使用 */
                {
                    const hmi_json_t* en = hmi_json_get(e, "enum");
                    size_t nen;

                    if (en != NULL) {
                        if (hmi_json_type(en) != HMI_JSON_ARR) {
                            errf(err, errsz, "tags.json", where,
                                 "enum 必须是数组");
                            goto fail;
                        }
                        nen = hmi_json_count(en);
                        if (nen == 0 || nen > 16) {
                            errf(err, errsz, "tags.json", where,
                                 "enum 项数 %zu 超出 1..16", nen);
                            goto fail;
                        }
                        t->enums = calloc(nen, sizeof(*t->enums));
                        if (!t->enums) {
                            errf(err, errsz, "tags.json", where, "内存不足");
                            goto fail;
                        }
                        t->enum_count = (int)nen;
                        for (size_t k = 0; k < nen; k++) {
                            const hmi_json_t* it = hmi_json_at(en, k);
                            const char* txt =
                                hmi_json_str(hmi_json_get(it, "text"), NULL);

                            if (!txt || !*txt ||
                                strlen(txt) >= sizeof(t->enums[k].text)) {
                                errf(err, errsz, "tags.json", where,
                                     "enum 第 %zu 项 text 缺失或过长", k);
                                goto fail;
                            }
                            t->enums[k].v = hmi_json_num(hmi_json_get(it, "v"), 0);
                            snprintf(t->enums[k].text, sizeof(t->enums[k].text),
                                     "%s", txt);
                        }
                    }
                }
                /* 37 §5 一致性：coil/discrete 的 datatype 必须是 bool；
                 * discrete/input 只读。sim 用同一套内存模型。 */
                if ((t->src_area == AREA_COIL || t->src_area == AREA_DISCRETE) &&
                    t->src_datatype != DT_BOOL) {
                    errf(err, errsz, "tags.json", where,
                         "area %s 要求 datatype=bool", area);
                    goto fail;
                }
                if ((t->src_area == AREA_DISCRETE || t->src_area == AREA_INPUT) &&
                    t->access_rw) {
                    errf(err, errsz, "tags.json", where,
                         "area %s 是只读区，access 不能是 rw", area);
                    goto fail;
                }
                /* v1 无 scale/offset，工程值即原始值：数值类型之间可互配，
                 * bool/string 不做转换（37 §5 一致性规则） */
                if (!type_compatible(t->type, t->src_datatype)) {
                    errf(err, errsz, "tags.json", where,
                         "type %s 与 source.datatype %s 不可转换",
                         hmi_json_str(hmi_json_get(e, "type"), "?"), dt);
                    goto fail;
                }
                t->src_dev_sim =
                    strcmp(project_device_protocol(p, dev), "sim") == 0 ? 1 : 0;
                t->has_source = 1;
            }
        }
    }

    hmi_json_free(a);
    return 0;

fail:
    hmi_json_free(a);
    return -1;   /* 不在此处释放 p：project_load 统一收尾，调用方不再二次 free */
}

void project_apply_tags(const project_t* p)
{
    for (int i = 0; i < p->tags_count; i++) {
        tag_t* t = tagdb_at(i);
        const tag_def_t* d = &p->tags[i];
        if (!t) continue;
        t->type = d->type;
        t->access_rw = (uint8_t)(d->access_rw ? 1 : 0);
        t->has_min = d->has_min;
        t->has_max = d->has_max;
        t->dmin = d->dmin;
        t->dmax = d->dmax;
        /* 内部变量（没有 source）的初值 0 是有效值；设备变量要等第一次读回 */
        t->quality = d->has_source ? TAG_Q_UNINIT : TAG_Q_GOOD;
        if (d->type == TAG_T_STRING && d->src_len > 0)
            (void)tagdb_string_bind(d->id, d->src_len);
    }
}

/* ── 工程根解析与总装 ── */

int project_resolve_root(char* root, size_t rootsz, char* err, size_t errsz)
{
    const char* env = getenv("HMI_PROJECT_DIR");
    char path[300];

    if (env && *env) {
        /* 宿主调试：直接指向工程目录 */
        if (snprintf(root, rootsz, "%s", env) >= (int)rootsz) {
            snprintf(err, errsz, "HMI_PROJECT_DIR 路径过长");
            return PROJECT_RESOLVE_ERR;
        }
        snprintf(path, sizeof(path), "%s/manifest.json", root);
        if (!file_exists(path)) {
            snprintf(err, errsz, "HMI_PROJECT_DIR 指向的目录没有 manifest.json：%s", root);
            return PROJECT_RESOLVE_ERR;
        }
        return PROJECT_RESOLVE_OK;
    }

    {
        const char* id = hmi_import_active_id();
        if (!id) {
            snprintf(err, errsz, "没有活动工程（%s/active 缺失或为空）", HMI_PROJECTS_DIR);
            return PROJECT_RESOLVE_NONE;
        }
        if (snprintf(root, rootsz, "%s/%s", HMI_PROJECTS_DIR, id) >= (int)rootsz) {
            snprintf(err, errsz, "活动工程路径过长");
            return PROJECT_RESOLVE_ERR;
        }
        snprintf(path, sizeof(path), "%s/manifest.json", root);
        if (!file_exists(path)) {
            snprintf(err, errsz, "活动工程 %s 的目录缺失 manifest.json", id);
            return PROJECT_RESOLVE_ERR;
        }
    }
    return PROJECT_RESOLVE_OK;
}

int project_load(const char* root, project_t* p, char* err, size_t errsz)
{
    char page_path[300];

    memset(p, 0, sizeof(*p));
    snprintf(p->root, sizeof(p->root), "%s", root);

    if (load_manifest(root, p, err, errsz) != 0) { project_free(p); return -1; }
    if (load_ports(root, p, err, errsz) != 0) { project_free(p); return -1; }
    if (load_devices(root, p, err, errsz) != 0) { project_free(p); return -1; }
    if (load_tags(root, p, err, errsz) != 0) { project_free(p); return -1; }

    /* 起始页文件必须存在（内容由 page_mgr 进入时解析） */
    if (project_page_path(p, p->manifest.start_page, page_path,
                          sizeof(page_path)) != 0 ||
        !file_exists(page_path)) {
        errf(err, errsz, "pages", p->manifest.start_page,
             "起始页文件缺失（%s）", page_path);
        project_free(p);
        return -1;
    }

    /* counts 与实际不一致只警告（37 §8：以实际解析结果为准）。运行时不预分配
     * 依赖它；pages/scripts 不比对——前者要遍历目录，后者 v1 无对应文件。 */
    if (p->manifest.has_counts) {
        if (p->manifest.counts_tags >= 0 &&
            p->manifest.counts_tags != p->tags_count)
            HMI_LOG("loader: manifest.counts.tags=%d，实际 %d（以实际为准）",
                    p->manifest.counts_tags, p->tags_count);
        if (p->manifest.counts_devices >= 0 &&
            p->manifest.counts_devices != p->devices_count)
            HMI_LOG("loader: manifest.counts.devices=%d，实际 %d（以实际为准）",
                    p->manifest.counts_devices, p->devices_count);
    }
    return 0;
}

void project_free(project_t* p)
{
    if (!p) return;
    for (int i = 0; i < p->tags_count; i++) free(p->tags[i].enums);
    free(p->tags);
    p->tags = NULL;
    p->tags_count = 0;
    p->devices_count = 0;
    p->ports_count = 0;
}

int project_page_path(const project_t* p, const char* page_id,
                      char* out, size_t outsz)
{
    int n = snprintf(out, outsz, "%s/pages/%s.json", p->root, page_id);
    if (n < 0 || (size_t)n >= outsz) return -1;
    return 0;
}

const tag_def_t* project_tag(const project_t* p, int id)
{
    if (!p || id < 0 || id >= p->tags_count) return NULL;
    return &p->tags[id];
}

int project_poll_ms(const project_t* p)
{
    for (int i = 0; i < p->devices_count; i++) {
        if (strcmp(p->devices[i].protocol, "sim") == 0)
            return p->devices[i].poll_ms;
    }
    return 200;
}
