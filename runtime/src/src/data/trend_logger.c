/*
 * trend_logger.c — 趋势采集线程
 */
#include "trend_logger.h"

#include "core/tagdb.h"
#include "hmi_json.h"
#include "hmi_log.h"
#include "trend_store.h"

#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#define TREND_GROUP_MAX 4
#define TREND_TAG_MAX   TREND_CH_MAX

typedef struct {
    char         id[32];
    int          interval_ms;
    int          ntag;
    int          tags[TREND_TAG_MAX];
    trend_file_t file;
    pthread_t    tid;
    volatile int run;
    int          started;
} trend_group_t;

static trend_group_t s_groups[TREND_GROUP_MAX];
static int s_ngroups;
static char s_dir[192];

static void* group_thread(void* arg)
{
    trend_group_t* g = (trend_group_t*)arg;
    float vals[TREND_CH_MAX];

    while (g->run) {
        for (int i = 0; i < g->ntag; i++) {
            const tag_t* t = tagdb_at(g->tags[i]);

            if (!t || tagdb_quality(g->tags[i]) != TAG_Q_GOOD) {
                /* 质量不好就别把坏值写进历史——历史一旦脏了没法分辨 */
                vals[i] = 0.0f;
            } else if (t->type == TAG_T_BOOL) {
                vals[i] = tagdb_bool(g->tags[i]) ? 1.0f : 0.0f;
            } else {
                vals[i] = (float)tagdb_num(g->tags[i]);
            }
        }
        if (trend_append(&g->file, (uint32_t)time(NULL), vals) != 0)
            HMI_LOG("trend %s: 写盘失败（继续采集）", g->id);
        usleep((useconds_t)g->interval_ms * 1000u);
    }
    return NULL;
}

int trend_logger_group(const char* id, int* channels, const int** tags)
{
    for (int i = 0; i < s_ngroups; i++) {
        if (strcmp(s_groups[i].id, id) != 0) continue;
        if (channels) *channels = s_groups[i].ntag;
        if (tags) *tags = s_groups[i].tags;
        return 0;
    }
    return -1;
}

int trend_logger_start(const project_t* p, const char* data_dir)
{
    char path[320], err[200];
    char* text;
    size_t len = 0;
    hmi_json_t* arr;
    size_t n;

    s_ngroups = 0;
    snprintf(s_dir, sizeof(s_dir), "%s", data_dir ? data_dir : "/data/chenge");

    snprintf(path, sizeof(path), "%s/trends.json", p->root);
    text = loader_read_text(path, 64 * 1024, &len);
    if (!text) return 0;                    /* 没有配置是正常情况 */

    arr = hmi_json_parse(text, len, err, sizeof(err));
    if (!arr) {
        HMI_LOG("trends.json 解析失败：%s", err);
        free(text);
        return -1;
    }
    if (hmi_json_type(arr) != HMI_JSON_ARR) {
        HMI_LOG("trends.json 根节点不是数组");
        hmi_json_free(arr);
        free(text);
        return -1;
    }

    n = hmi_json_count(arr);
    for (size_t i = 0; i < n && s_ngroups < TREND_GROUP_MAX; i++) {
        const hmi_json_t* e = hmi_json_at(arr, i);
        const char* id = hmi_json_str(hmi_json_get(e, "id"), NULL);
        const hmi_json_t* chs = hmi_json_get(e, "channels");
        long iv = hmi_json_int(hmi_json_get(e, "interval_ms"), 1000);
        long max_rec = hmi_json_int(hmi_json_get(e, "max_records"), 6000);
        trend_group_t* g = &s_groups[s_ngroups];
        size_t nch;

        if (!id || !*id) {
            HMI_LOG("trends.json 第 %zu 项缺 id", i);
            continue;
        }
        if (hmi_json_type(chs) != HMI_JSON_ARR) {
            HMI_LOG("trend %s: 缺 channels", id);
            continue;
        }
        nch = hmi_json_count(chs);
        if (nch < 1 || nch > TREND_TAG_MAX) {
            HMI_LOG("trend %s: channels 项数 %zu 越界（1..%d）", id, nch, TREND_TAG_MAX);
            continue;
        }
        memset(g, 0, sizeof(*g));
        snprintf(g->id, sizeof(g->id), "%s", id);
        g->interval_ms = (int)iv;
        for (size_t k = 0; k < nch; k++) {
            long tag = hmi_json_int(hmi_json_at(chs, k), -1);

            if (tag < 0 || tag >= tagdb_count()) {
                HMI_LOG("trend %s: 第 %zu 个通道的 tag %ld 不存在", id, k, tag);
                g->ntag = 0;
                break;
            }
            g->tags[g->ntag++] = (int)tag;
        }
        if (g->ntag == 0) continue;

        if (trend_open(&g->file, s_dir, g->id, g->ntag, (int)max_rec,
                       g->interval_ms, 0) != 0) {
            HMI_LOG("trend %s: 打开数据文件失败", id);
            continue;
        }
        g->run = 1;
        if (pthread_create(&g->tid, NULL, group_thread, g) != 0) {
            HMI_LOG("trend %s: 采集线程创建失败", id);
            g->run = 0;
            trend_close(&g->file);
            continue;
        }
        g->started = 1;
        s_ngroups++;
        HMI_LOG("trend %s: 每 %dms 采 %d 个通道，容量 %ld 条（%.0f 分钟）", id,
                g->interval_ms, g->ntag, max_rec,
                (double)max_rec * g->interval_ms / 60000.0);
    }

    hmi_json_free(arr);
    free(text);
    return 0;
}

void trend_logger_stop(void)
{
    for (int i = 0; i < s_ngroups; i++) {
        if (s_groups[i].started) {
            s_groups[i].run = 0;
            pthread_join(s_groups[i].tid, NULL);
            trend_close(&s_groups[i].file);
            s_groups[i].started = 0;
        }
    }
    s_ngroups = 0;
}
