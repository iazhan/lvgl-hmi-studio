/*
 * savecfg.c — 存盘组（ADR-0014）
 *
 * 文件格式复用 trend_store（TDB1 环形文件），所以历史控件不用改读取路径。
 * 采样在主循环里做（100ms 扫描），不开线程：存盘组周期粒度是秒，且模式有"值变化/触发"两种，
 * 塞不进 trend_logger 那种固定周期线程。
 */
#include "savecfg.h"
#include "trend_store.h"
#include "core/audit.h"
#include "core/tagdb.h"
#include "hmi_json.h"
#include "hmi_log.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct {
    char         name[SAVECFG_NAME_MAX];
    int          mode;            /* 0 period、1 change、2 trigger */
    int          period_s;
    int          max_mb;
    int          max_rec;         /* 环形容量（条）：max_records 明写时以它为准 */
    int          full_policy;     /* 0 overwrite（缺省）、1 stop */
    int          full_stopped;    /* policy=stop 且写满：停写，只记一次日志 */
    int          rec_ts;          /* CSV 是否带时间列（落盘格式里 ts 总是有） */
    int          unix_ts;         /* CSV 时间列：1 = unix 秒，0 = YYYY-MM-DD HH:MM:SS */
    char         delim;           /* CSV 分隔符 */
    char         path[192];       /* 该组的存储根（空 = 用 s_dir） */
    int          tags[SAVECFG_MAX_TAGS];
    int          ntags;
    trend_file_t f;
    uint32_t     last_ts;
    float        last[TREND_CH_MAX];
    int          has_last;
    int          opened;
} savecfg_group_t;

static savecfg_group_t s_groups[SAVECFG_MAX_GROUPS];
static int             s_n;
static const project_t* s_proj;      /* 只为 CSV 表头取变量名 */

/* 总配额压缩系数：分子/分母都非 0 时生效（0 = 不限） */
static int             s_quota_scale_num;
static int             s_quota_scale_den;
static char            s_dir[192] = "/data/chenge";      /* 数据根（trend_open 自己拼 trends/） */
static char            s_storage[192];                   /* 根上的 storage_path，空 = 不指定 */

static int read_file(const char* path, char** out, size_t* len)
{
    FILE* f = fopen(path, "rb");
    long n;

    if (!f) return -1;
    fseek(f, 0, SEEK_END);
    n = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (n <= 0 || n > 256 * 1024) {
        fclose(f);
        return -1;
    }
    *out = malloc((size_t)n + 1);
    if (!*out) {
        fclose(f);
        return -1;
    }
    if (fread(*out, 1, (size_t)n, f) != (size_t)n) {
        free(*out);
        *out = NULL;
        fclose(f);
        return -1;
    }
    (*out)[n] = '\0';
    *len = (size_t)n;
    fclose(f);
    return 0;
}

/* storage_path 只允许落在数据目录或 /data/userdata 之下：组态把文件写进系统卷会写坏设备，
 * 路径穿越同理。不合法按加载错误处理（不静默改回缺省）。 */
static int path_ok(const char* path)
{
    static const char* const roots[] = { "/data/userdata" };
    size_t len, n;

    if (!path || path[0] != '/') return 0;
    len = strlen(path);
    if (strstr(path, "//") || strstr(path, "/../") ||
        (len >= 3 && strcmp(path + len - 3, "/..") == 0))
        return 0;
    n = strlen(s_dir);
    if (strncmp(path, s_dir, n) == 0 && (path[n] == '\0' || path[n] == '/')) return 1;
    for (int i = 0; i < (int)(sizeof(roots) / sizeof(roots[0])); i++) {
        n = strlen(roots[i]);
        if (strncmp(path, roots[i], n) == 0 && (path[n] == '\0' || path[n] == '/')) return 1;
    }
    return 0;
}

/* CSV 分隔符：, ; | tab（也认 "\t"），其它按逗号并记一行日志 */
static char parse_delim(const char* s, const char* gname)
{
    if (!s || !*s) return ',';
    if (strcmp(s, "tab") == 0 || strcmp(s, "\\t") == 0) return '\t';
    if (!s[1]) {
        if (s[0] == ',' || s[0] == ';' || s[0] == '|' || s[0] == '\t') return s[0];
    }
    HMI_LOG("savecfg: 组 %s 的 csv_delimiter 不认（, ; | tab），按逗号导出", gname ? gname : "?");
    return ',';
}

static void collect(int gi, float* vals)
{
    for (int i = 0; i < s_groups[gi].ntags; i++)
        vals[i] = (float)tagdb_num(s_groups[gi].tags[i]);
}

/* 落一条；写满且 full_policy=stop 时返回 -1（不写，日志只记一次） */
static int append_sample(int gi, const float* vals)
{
    savecfg_group_t* g = &s_groups[gi];
    uint32_t ts = (uint32_t)time(NULL);

    if (!g->opened) return -1;
    if (g->full_policy == 1 && g->f.count >= (uint32_t)g->f.max_rec) {
        if (!g->full_stopped) {
            g->full_stopped = 1;
            HMI_LOG("savecfg: 组 %s 已满（%d 条，full_policy=stop），停止采集", g->name,
                    g->f.max_rec);
        }
        return -1;
    }
    if (trend_append(&g->f, ts, vals) != 0) {
        HMI_LOG("savecfg: 写 %s 失败", g->name);
        return -1;
    }
    g->last_ts = ts;
    memcpy(g->last, vals, sizeof(float) * (size_t)g->ntags);
    g->has_last = 1;
    return 0;
}

int savecfg_start(const project_t* p, const char* data_dir)
{
    char path[320];
    char* text = NULL;
    size_t len = 0;
    hmi_json_t* root;
    const hmi_json_t* groups;
    size_t n;

    savecfg_stop();
    /* trend_open 自己会拼 trends/ 子目录，这里给根目录（给成 <data>/trends 会变成 trends/trends） */
    if (data_dir && *data_dir) snprintf(s_dir, sizeof(s_dir), "%s", data_dir);
    s_proj = p;
    s_storage[0] = '\0';
    if (!p) return 0;
    snprintf(path, sizeof(path), "%s/savecfg.json", p->root);
    if (read_file(path, &text, &len) != 0) return 0;

    {
        char jerr[128] = { 0 };

        root = hmi_json_parse(text, len, jerr, sizeof(jerr));
    }
    if (!root) {
        free(text);
        HMI_LOG("savecfg: savecfg.json 解析失败，存盘组不启用");
        return -1;
    }
    groups = hmi_json_get(root, "groups");
    n = hmi_json_count(groups);
    if (hmi_json_type(groups) != HMI_JSON_ARR || n > SAVECFG_MAX_GROUPS) {
        HMI_LOG("savecfg: groups 项数 %zu 超过上限 %d", n, SAVECFG_MAX_GROUPS);
        hmi_json_free(root);
        free(text);
        return -1;
    }
    /* 根上的 storage_path：各组缺省都落在这里；不合法直接判加载失败，不改回缺省 */
    {
        const char* sp = hmi_json_str(hmi_json_get(root, "storage_path"), NULL);

        if (sp && *sp) {
            if (!path_ok(sp)) {
                HMI_LOG("savecfg: storage_path=%s 不在允许范围内（数据目录或 /data/userdata 之下）", sp);
                hmi_json_free(root);
                free(text);
                return -1;
            }
            snprintf(s_storage, sizeof(s_storage), "%s", sp);
            HMI_LOG("savecfg: 存储根改到 %s", s_storage);
        }
    }
    /* 总配额（34 §9.2）：所有存盘组加起来不超过 total_max_mb。
     * 超了就按比例压缩各组的 max_mb——不改落盘格式，也不删已有数据，
     * 只是让新环形容量之和落在配额内。0 = 不限。 */
    {
        int total_mb = (int)hmi_json_int(hmi_json_get(root, "total_max_mb"), 0);
        int sum_mb = 0;

        if (total_mb > 0) {
            for (size_t i = 0; i < n; i++) {
                const hmi_json_t* j = hmi_json_at(groups, i);
                const char* nm = hmi_json_str(hmi_json_get(j, "name"), NULL);
                const char* md = hmi_json_str(hmi_json_get(j, "mode"), "period");

                if (!nm || !*nm) continue;
                if (strcmp(md, "period") != 0 && strcmp(md, "change") != 0 &&
                    strcmp(md, "trigger") != 0)
                    continue;
                sum_mb += (int)hmi_json_int(hmi_json_get(j, "max_mb"), 1);
            }
            if (sum_mb > total_mb) {
                s_quota_scale_num = total_mb;
                s_quota_scale_den = sum_mb;
                HMI_LOG("savecfg: 各组 max_mb 合计 %d 超过 total_max_mb=%d，按比例压缩",
                        sum_mb, total_mb);
            }
        }
    }
    for (size_t i = 0; i < n; i++) {
        const hmi_json_t* j = hmi_json_at(groups, i);
        const char* name = hmi_json_str(hmi_json_get(j, "name"), NULL);
        const char* mode = hmi_json_str(hmi_json_get(j, "mode"), "period");
        const hmi_json_t* tags = hmi_json_get(j, "tags");
        savecfg_group_t* g = &s_groups[s_n];
        int max_rec;
        size_t nt;

        if (!name || !*name || strlen(name) >= SAVECFG_NAME_MAX || strchr(name, '/')) {
            HMI_LOG("savecfg: 第 %zu 组 name 非法（空/过长/含斜杠）", i);
            continue;
        }
        memset(g, 0, sizeof(*g));
        snprintf(g->name, sizeof(g->name), "%s", name);
        if (strcmp(mode, "period") == 0) g->mode = 0;
        else if (strcmp(mode, "change") == 0) g->mode = 1;
        else if (strcmp(mode, "trigger") == 0) g->mode = 2;
        else {
            HMI_LOG("savecfg: 组 %s 的 mode=%s 非法（period/change/trigger）", name, mode);
            continue;
        }
        g->period_s = (int)hmi_json_int(hmi_json_get(j, "period_s"), 0);
        g->max_mb = (int)hmi_json_int(hmi_json_get(j, "max_mb"), 1);
        if (g->mode == 0 && (g->period_s < 1 || g->period_s > 3600)) {
            HMI_LOG("savecfg: 组 %s 的 period_s=%d 越界（1..3600）", name, g->period_s);
            continue;
        }
        if (g->max_mb < 1) g->max_mb = 1;
        if (g->max_mb > 64) g->max_mb = 64;
        if (s_quota_scale_den > 0) {
            int scaled = (int)((long)g->max_mb * s_quota_scale_num / s_quota_scale_den);

            if (scaled < 1) scaled = 1;
            if (scaled < g->max_mb)
                HMI_LOG("savecfg: 组 %s 的 max_mb %d 按总配额压到 %d", name, g->max_mb, scaled);
            g->max_mb = scaled;
        }
        if (hmi_json_type(tags) != HMI_JSON_ARR || hmi_json_count(tags) < 1 ||
            hmi_json_count(tags) > SAVECFG_MAX_TAGS) {
            HMI_LOG("savecfg: 组 %s 的 tags 项数应为 1..%d", name, SAVECFG_MAX_TAGS);
            continue;
        }
        nt = hmi_json_count(tags);
        for (size_t k = 0; k < nt; k++) {
            long id = hmi_json_int(hmi_json_at(tags, k), -1);

            if (id < 0 || id >= tagdb_count()) {
                HMI_LOG("savecfg: 组 %s 的 tags[%zu]=%ld 不是有效变量", name, k, id);
                g->ntags = 0;
                break;
            }
            g->tags[g->ntags++] = (int)id;
        }
        if (g->ntags != (int)nt) continue;

        /* 写满怎么办、存到哪、CSV 怎么切：都在组上配（37 §6.9） */
        {
            const char* fp = hmi_json_str(hmi_json_get(j, "full_policy"), "overwrite");
            const char* sp = hmi_json_str(hmi_json_get(j, "storage_path"), NULL);
            long mr = hmi_json_int(hmi_json_get(j, "max_records"), 0);

            if (strcmp(fp, "overwrite") == 0) {
                g->full_policy = 0;
            } else if (strcmp(fp, "stop") == 0) {
                g->full_policy = 1;
            } else {
                HMI_LOG("savecfg: 组 %s 的 full_policy=%s 非法（overwrite/stop）", name, fp);
                continue;
            }
            if (sp && *sp) {
                if (!path_ok(sp)) {
                    HMI_LOG("savecfg: 组 %s 的 storage_path=%s 不在允许范围内"
                            "（数据目录或 /data/userdata 之下）", name, sp);
                    continue;
                }
                snprintf(g->path, sizeof(g->path), "%s", sp);
            } else {
                snprintf(g->path, sizeof(g->path), "%s", s_storage);
            }
            g->delim = parse_delim(hmi_json_str(hmi_json_get(j, "csv_delimiter"), NULL), name);
            g->rec_ts = hmi_json_bool(hmi_json_get(j, "record_timestamp"), 1);
            {
                const char* tf = hmi_json_str(hmi_json_get(j, "csv_time_format"), "iso");

                if (strcmp(tf, "iso") == 0) {
                    g->unix_ts = 0;
                } else if (strcmp(tf, "unix") == 0) {
                    g->unix_ts = 1;
                } else {
                    HMI_LOG("savecfg: 组 %s 的 csv_time_format=%s 非法（iso/unix）", name, tf);
                    continue;
                }
            }
            if (mr > 0) {
                if (mr < 2) mr = 2;
                if (mr > 200000) mr = 200000;
                g->max_rec = (int)mr;
            }
        }

        /* 记录长度 = ts(4) + ch*4；没写 max_records 时按 max_mb 换算环形容量 */
        if (g->max_rec > 0) {
            max_rec = g->max_rec;
            HMI_LOG("savecfg: 组 %s 用 max_records=%d 条（%d 通道，约 %d KB）", name, max_rec,
                    g->ntags, max_rec * (4 + 4 * g->ntags) / 1024);
        } else {
            max_rec = (int)((long)g->max_mb * 1024 * 1024 / (4 + 4L * g->ntags));
            if (max_rec < 16) max_rec = 16;
            if (max_rec > 200000) max_rec = 200000;
        }

        if (trend_open(&g->f, g->path[0] ? g->path : s_dir, g->name, g->ntags, max_rec,
                       1000, 0) != 0) {      /* interval 只是文件头，采样由本模块决定 */
            HMI_LOG("savecfg: 组 %s 打开文件失败", name);
            continue;
        }
        g->opened = 1;
        s_n++;
    }
    hmi_json_free(root);
    free(text);
    if (s_n > 0) HMI_LOG("savecfg: %d 个存盘组", s_n);
    return s_n;
}

void savecfg_stop(void)
{
    for (int i = 0; i < s_n; i++)
        if (s_groups[i].opened) trend_close(&s_groups[i].f);
    s_n = 0;
}

void savecfg_tick(void)
{
    uint32_t now = (uint32_t)time(NULL);

    for (int i = 0; i < s_n; i++) {
        savecfg_group_t* g = &s_groups[i];
        float vals[TREND_CH_MAX];

        if (!g->opened || g->mode == 2) continue;
        collect(i, vals);
        if (g->mode == 0) {
            if (g->has_last && now - g->last_ts < (uint32_t)g->period_s) continue;
            append_sample(i, vals);
        } else {                                  /* change：任一通道变了就落一条 */
            int changed = !g->has_last;

            for (int k = 0; k < g->ntags && !changed; k++)
                if (fabsf(vals[k] - g->last[k]) > 1e-6f) changed = 1;
            if (changed) append_sample(i, vals);
        }
    }
}

int savecfg_sample_now(const char* name)
{
    int wrote = 0;

    if (s_n == 0) return -1;
    for (int i = 0; i < s_n; i++) {
        float vals[TREND_CH_MAX];

        if (name && *name && strcmp(s_groups[i].name, name) != 0) continue;
        collect(i, vals);
        if (append_sample(i, vals) == 0) wrote++;
    }
    if (wrote == 0 && name && *name) return -1;
    HMI_LOG("savecfg: save_now 写入 %d 组", wrote);
    if (name && *name) audit_log("config-save", NULL, "%d 组（组 %s）", wrote, name);
    else audit_log("config-save", NULL, "%d 组", wrote);
    return wrote;
}

int savecfg_group(const char* id, int* channels, const int** tags)
{
    if (!id || !*id) return -1;
    for (int i = 0; i < s_n; i++) {
        if (strcmp(s_groups[i].name, id) != 0) continue;
        if (channels) *channels = s_groups[i].ntags;
        if (tags) *tags = s_groups[i].tags;
        return 0;
    }
    return -1;
}

int savecfg_count(void)
{
    return s_n;
}

/* CSV 字段：含分隔符或引号时加引号（变量名里可能有逗号） */
static void csv_field(FILE* f, const char* s, char d)
{
    const char* p;

    if (!s) s = "";
    if (!strchr(s, d) && !strchr(s, '"')) {
        fputs(s, f);
        return;
    }
    fputc('"', f);
    for (p = s; *p; p++) {
        if (*p == '"') fputc('"', f);
        fputc(*p, f);
    }
    fputc('"', f);
}

int savecfg_export_csv(const char* name, int last_min, uint32_t from_ts, uint32_t to_ts,
                       const char* dir, char* err, size_t errsz)
{
    savecfg_group_t* g = NULL;
    trend_rec_t* recs = NULL;
    int ch = 0, iv = 0, n, rows = 0;
    uint32_t lo = 0, hi = 0;
    char path[400];
    time_t now = time(NULL);
    struct tm tmv;
    FILE* f;

    if (!name || !*name) {
        snprintf(err, errsz, "没指定存盘组");
        return -1;
    }
    for (int i = 0; i < s_n && !g; i++)
        if (strcmp(s_groups[i].name, name) == 0) g = &s_groups[i];
    if (!g) {
        snprintf(err, errsz, "存盘组 %s 不存在", name);
        return -1;
    }
    if (!dir || !*dir) {
        snprintf(err, errsz, "导出目录为空");
        return -1;
    }
    if (from_ts && to_ts && from_ts > to_ts) {
        uint32_t t = from_ts;

        from_ts = to_ts;
        to_ts = t;
    }
    if (last_min > 0) lo = (uint32_t)(now > (time_t)last_min * 60 ? now - (time_t)last_min * 60 : 0);
    else if (from_ts) lo = from_ts;
    if (to_ts) hi = to_ts;

    n = trend_read_all(g->path[0] ? g->path : s_dir, g->name, &recs, &ch, &iv);
    if (n < 0) {
        snprintf(err, errsz, "读不到组 %s 的记录文件", g->name);
        return -1;
    }

    localtime_r(&now, &tmv);
    snprintf(path, sizeof(path), "%s/%s_%04d%02d%02d_%02d%02d%02d.csv", dir, g->name,
             tmv.tm_year + 1900, tmv.tm_mon + 1, tmv.tm_mday, tmv.tm_hour, tmv.tm_min,
             tmv.tm_sec);
    f = fopen(path, "w");
    if (!f) {
        snprintf(err, errsz, "建不了 %s", path);
        free(recs);
        return -1;
    }

    /* 表头：时间列（可关）+ 变量名。通道按文件头里的 ch 走，组配置改过也不会越界 */
    if (g->rec_ts) fputs("time", f);
    for (int i = 0; i < g->ntags && i < ch; i++) {
        const tag_def_t* td = s_proj ? project_tag(s_proj, g->tags[i]) : NULL;
        char buf[48];

        if (g->rec_ts || i > 0) fputc(g->delim, f);
        snprintf(buf, sizeof(buf), "%s", (td && td->name[0]) ? td->name : "tag");
        csv_field(f, buf, g->delim);
    }
    fputc('\n', f);

    for (int i = 0; i < n; i++) {
        time_t t = (time_t)recs[i].ts;
        struct tm rt;

        if (lo && recs[i].ts < lo) continue;
        if (hi && recs[i].ts > hi) continue;
        localtime_r(&t, &rt);
        if (g->rec_ts) {
            if (g->unix_ts)
                fprintf(f, "%lu", (unsigned long)recs[i].ts);
            else
                fprintf(f, "%04d-%02d-%02d %02d:%02d:%02d", rt.tm_year + 1900, rt.tm_mon + 1,
                        rt.tm_mday, rt.tm_hour, rt.tm_min, rt.tm_sec);
        }
        for (int c = 0; c < g->ntags && c < ch; c++) {
            if (g->rec_ts || c > 0) fputc(g->delim, f);
            fprintf(f, "%.6g", recs[i].v[c]);
        }
        fputc('\n', f);
        rows++;
    }
    fclose(f);
    free(recs);
    HMI_LOG("savecfg: 导出组 %s → %s（%d 行，区间 %s）", g->name, path, rows,
            (lo || hi) ? "有限定" : "全部");
    HMI_LOG("savecfg: 组 %s 共 %d 条记录（环形容量 %d）", g->name, n, g->f.max_rec);
    return rows;
}
