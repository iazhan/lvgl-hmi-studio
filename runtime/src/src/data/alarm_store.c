/*
 * alarm_store.c — 报警事件文件（ADR-0008 决定 5）
 *
 * 环形文件，写满覆盖最旧一条；每次追加只改记录 + 头里的 head/count。
 * 与 trend_store 同一套写法，但记录更小（12 字节），文件名用组名哈希。
 */
#include "alarm_store.h"
#include "hmi_log.h"

#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#define ALM_HDR_SIZE   32
#define ALM_REC_SIZE   12
#define ALM_MAGIC      "ALM1"
#define ALM_MAX_REC    5000
#define ALM_MAX_FILES  8

typedef struct {
    char     group[ALARM_GROUP_MAX];
    uint32_t hash;
    int      fd;
    int      max_rec;
    uint32_t head;
    uint32_t count;
} alm_file_t;

static char      s_dir[192] = "/data/chenge";
static alm_file_t s_files[ALM_MAX_FILES];
static int       s_nfiles;

void alarm_store_set_dir(const char* dir)
{
    if (!dir || !*dir) return;
    snprintf(s_dir, sizeof(s_dir), "%s", dir);
    for (int i = 0; i < s_nfiles; i++) {
        if (s_files[i].fd >= 0) close(s_files[i].fd);
        s_files[i].fd = -1;
    }
    s_nfiles = 0;
}

/* FNV-1a：组名可能是中文，直接当文件名在 FAT 上不可靠 */
static uint32_t group_hash(const char* s)
{
    uint32_t h = 2166136261u;

    while (*s) {
        h ^= (uint32_t)(unsigned char)*s++;
        h *= 16777619u;
    }
    return h;
}

static uint32_t rd32(const uint8_t* p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static void wr32(uint8_t* p, uint32_t v)
{
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
    p[2] = (uint8_t)((v >> 16) & 0xFF);
    p[3] = (uint8_t)((v >> 24) & 0xFF);
}

static void mkdir_p(const char* path)
{
    char tmp[256];
    size_t n;

    snprintf(tmp, sizeof(tmp), "%s", path);
    n = strlen(tmp);
    for (size_t i = 1; i < n; i++) {
        if (tmp[i] != '/') continue;
        tmp[i] = '\0';
        mkdir(tmp, 0755);
        tmp[i] = '/';
    }
    mkdir(tmp, 0755);
}

static void build_path(char* out, size_t outsz, const char* group)
{
    snprintf(out, outsz, "%s/alarms/alm_%08x.bin", s_dir, group_hash(group));
}

static int write_header(alm_file_t* f)
{
    uint8_t h[ALM_HDR_SIZE];

    memset(h, 0, sizeof(h));
    memcpy(h, ALM_MAGIC, 4);
    h[4] = 1;                                   /* ver */
    h[6] = (uint8_t)(ALM_REC_SIZE & 0xFF);
    h[7] = (uint8_t)((ALM_REC_SIZE >> 8) & 0xFF);
    wr32(h + 8, (uint32_t)f->max_rec);
    wr32(h + 12, f->head);
    wr32(h + 16, f->count);
    wr32(h + 20, f->hash);
    if (pwrite(f->fd, h, sizeof(h), 0) != (ssize_t)sizeof(h)) return -1;
    return 0;
}

static alm_file_t* get_file(const char* group)
{
    char path[256];
    uint8_t h[ALM_HDR_SIZE];
    alm_file_t* f;
    int fd;
    ssize_t n;

    if (!group || !*group) return NULL;
    for (int i = 0; i < s_nfiles; i++)
        if (strcmp(s_files[i].group, group) == 0) return &s_files[i];
    if (s_nfiles >= ALM_MAX_FILES) return NULL;

    f = &s_files[s_nfiles];
    memset(f, 0, sizeof(*f));
    snprintf(f->group, sizeof(f->group), "%s", group);
    f->hash = group_hash(group);
    f->max_rec = ALM_MAX_REC;
    snprintf(path, sizeof(path), "%s/alarms", s_dir);
    mkdir_p(path);
    build_path(path, sizeof(path), group);
    fd = open(path, O_RDWR | O_CREAT, 0644);
    if (fd < 0) {
        HMI_LOG("alarm: 打不开事件文件 %s（%s）", path, strerror(errno));
        return NULL;
    }
    f->fd = fd;
    n = pread(fd, h, sizeof(h), 0);
    if (n != (ssize_t)sizeof(h) || memcmp(h, ALM_MAGIC, 4) != 0 ||
        rd32(h + 20) != f->hash || (int)rd32(h + 8) != f->max_rec) {
        if (ftruncate(fd, 0) != 0) { /* 继续，下面写头会覆盖 */ }
        f->head = 0;
        f->count = 0;
        if (write_header(f) != 0) {
            close(fd);
            f->fd = -1;
            return NULL;
        }
    } else {
        f->head = rd32(h + 12);
        f->count = rd32(h + 16);
        if (f->head >= (uint32_t)f->max_rec) f->head = 0;
        if (f->count > (uint32_t)f->max_rec) f->count = (uint32_t)f->max_rec;
    }
    s_nfiles++;
    return f;
}

int alarm_store_append(const char* group, const alarm_event_t* ev)
{
    alm_file_t* f = get_file(group);
    uint8_t rec[ALM_REC_SIZE];
    off_t off;

    if (!f || f->fd < 0 || !ev) return -1;
    wr32(rec, ev->ts);
    rec[4] = (uint8_t)(ev->rule_id & 0xFF);
    rec[5] = (uint8_t)((ev->rule_id >> 8) & 0xFF);
    rec[6] = ev->event;
    rec[7] = ev->level;
    memcpy(rec + 8, &ev->value, 4);
    off = (off_t)ALM_HDR_SIZE + (off_t)f->head * ALM_REC_SIZE;
    if (pwrite(f->fd, rec, ALM_REC_SIZE, off) != (ssize_t)ALM_REC_SIZE) return -1;
    f->head = (f->head + 1) % (uint32_t)f->max_rec;
    if (f->count < (uint32_t)f->max_rec) f->count++;
    return write_header(f);
}

int alarm_store_read(const char* group, uint32_t from, uint32_t to,
                     alarm_event_t* out, int max)
{
    uint8_t rec[ALM_REC_SIZE];
    char path[256];
    uint8_t h[ALM_HDR_SIZE];
    int fd, n = 0;
    uint32_t head, count, max_rec;

    if (!out || max < 1) return -1;
    build_path(path, sizeof(path), group);
    fd = open(path, O_RDONLY);
    if (fd < 0) return 0;                        /* 还没有事件文件 = 没有历史 */
    if (pread(fd, h, sizeof(h), 0) != (ssize_t)sizeof(h) ||
        memcmp(h, ALM_MAGIC, 4) != 0) {
        close(fd);
        return -1;
    }
    max_rec = rd32(h + 8);
    head = rd32(h + 12);
    count = rd32(h + 16);
    if (max_rec < 1 || max_rec > ALM_MAX_REC || count > max_rec) {
        close(fd);
        return -1;
    }
    /* 从最新往回读，凑够 max 或超出时间下限就停 */
    for (uint32_t i = 0; i < count && n < max; i++) {
        uint32_t idx = (head + max_rec - 1 - i) % max_rec;
        off_t off = (off_t)ALM_HDR_SIZE + (off_t)idx * ALM_REC_SIZE;
        uint32_t ts;

        if (pread(fd, rec, ALM_REC_SIZE, off) != (ssize_t)ALM_REC_SIZE) break;
        ts = rd32(rec);
        if (from && ts < from) break;
        if (to && ts > to) continue;
        out[n].ts = ts;
        out[n].rule_id = (uint16_t)(rec[4] | (rec[5] << 8));
        out[n].event = rec[6];
        out[n].level = rec[7];
        memcpy(&out[n].value, rec + 8, 4);
        n++;
    }
    close(fd);
    return n;
}
