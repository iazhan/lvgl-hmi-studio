/*
 * trend_store.c — 趋势数据文件（环形、定长记录）
 */
#include "trend_store.h"

#include "hmi_log.h"

#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

static void build_path(char* out, size_t outsz, const char* dir, const char* id)
{
    snprintf(out, outsz, "%s/trends/%s.bin", dir, id);
}

static void mkdir_p(const char* dir)
{
    char tmp[256];
    size_t n;

    snprintf(tmp, sizeof(tmp), "%s/trends", dir);
    n = strlen(tmp);
    for (size_t i = 1; i < n; i++) {
        if (tmp[i] != '/') continue;
        tmp[i] = '\0';
        mkdir(tmp, 0755);
        tmp[i] = '/';
    }
    mkdir(tmp, 0755);
}

static int write_header(const trend_file_t* f)
{
    uint8_t h[TREND_HDR_SIZE];

    memset(h, 0, sizeof(h));
    memcpy(h, TREND_MAGIC, 4);
    h[4] = 1;                                  /* ver 低字节 */
    h[6] = (uint8_t)(f->rec_size & 0xFF);       /* rec_size 小端 */
    h[7] = (uint8_t)((f->rec_size >> 8) & 0xFF);
    h[8] = (uint8_t)f->ch;
    h[12] = (uint8_t)(f->interval_ms & 0xFF);
    h[13] = (uint8_t)((f->interval_ms >> 8) & 0xFF);
    /* 容量是 u32：只写低 16 位的话，1 通道 1MB（131072 条）会被截成 0，读回来看不见任何记录 */
    h[16] = (uint8_t)(f->max_rec & 0xFF);
    h[17] = (uint8_t)((f->max_rec >> 8) & 0xFF);
    h[18] = (uint8_t)((f->max_rec >> 16) & 0xFF);
    h[19] = (uint8_t)((f->max_rec >> 24) & 0xFF);
    h[20] = (uint8_t)(f->head & 0xFF);
    h[21] = (uint8_t)((f->head >> 8) & 0xFF);
    h[24] = (uint8_t)(f->count & 0xFF);
    h[25] = (uint8_t)((f->count >> 8) & 0xFF);

    if (pwrite(f->fd, h, sizeof(h), 0) != (ssize_t)sizeof(h)) return -1;
    return 0;
}

static uint32_t rd32(const uint8_t* p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) |
           ((uint32_t)p[3] << 24);
}

int trend_open(trend_file_t* f, const char* dir, const char* id, int channels,
               int max_rec, int interval_ms, int truncate)
{
    uint8_t h[TREND_HDR_SIZE];
    ssize_t n;

    memset(f, 0, sizeof(*f));
    f->fd = -1;
    if (channels < 1 || channels > TREND_CH_MAX) return -1;
    if (max_rec < 2 || max_rec > 200000) return -1;
    if (interval_ms < 100 || interval_ms > 3600000) return -1;

    mkdir_p(dir);
    build_path(f->path, sizeof(f->path), dir, id);
    f->ch = channels;
    f->max_rec = max_rec;
    f->interval_ms = (uint32_t)interval_ms;
    f->rec_size = sizeof(uint32_t) + sizeof(float) * (size_t)channels;

    f->fd = open(f->path, O_RDWR | O_CREAT, 0644);
    if (f->fd < 0) {
        HMI_LOG("trend: 打不开 %s（%s）", f->path, strerror(errno));
        return -1;
    }

    n = pread(f->fd, h, sizeof(h), 0);
    /* 配置变了必须重建：容量变了旧记录还按旧容量排布（trend_append 每次都重写文件头，
     * 只是把 max_rec 改掉、数据仍按旧步长铺），读回来就是错位的；采样周期变了时间轴也对不上。 */
    if (!truncate && n == (ssize_t)sizeof(h) && memcmp(h, TREND_MAGIC, 4) == 0 &&
        (h[8] != (uint8_t)channels ||
         (int)rd32(h + 16) != max_rec ||
         (int)rd32(h + 12) != interval_ms)) {
        HMI_LOG("trend: %s 配置变化（通道 %u→%d、容量 %d→%d、周期 %u→%d），重建历史文件",
                f->path, (unsigned)h[8], channels, (int)rd32(h + 16), max_rec,
                (unsigned)rd32(h + 12), interval_ms);
        truncate = 1;
    }
    if (truncate || n != (ssize_t)sizeof(h) || memcmp(h, TREND_MAGIC, 4) != 0 ||
        h[8] != (uint8_t)channels) {
        if (ftruncate(f->fd, 0) != 0) { /* 继续，下面写头会覆盖 */ }
        f->head = 0;
        f->count = 0;
        if (write_header(f) != 0) {
            HMI_LOG("trend: 写头失败 %s", f->path);
            close(f->fd);
            f->fd = -1;
            return -1;
        }
    } else {
        f->head = rd32(h + 20);
        f->count = rd32(h + 24);
        if (f->head >= (uint32_t)f->max_rec) f->head = 0;
        if (f->count > (uint32_t)f->max_rec) f->count = (uint32_t)f->max_rec;
    }
    return 0;
}

void trend_close(trend_file_t* f)
{
    if (f && f->fd >= 0) {
        close(f->fd);
        f->fd = -1;
    }
}

int trend_append(trend_file_t* f, uint32_t ts, const float* vals)
{
    uint8_t rec[4 + 4 * TREND_CH_MAX];
    off_t off;

    if (!f || f->fd < 0) return -1;
    rec[0] = (uint8_t)(ts & 0xFF);
    rec[1] = (uint8_t)((ts >> 8) & 0xFF);
    rec[2] = (uint8_t)((ts >> 16) & 0xFF);
    rec[3] = (uint8_t)((ts >> 24) & 0xFF);
    for (int i = 0; i < f->ch; i++) {
        float v = vals[i];

        memcpy(rec + 4 + i * 4, &v, 4);
    }

    off = (off_t)TREND_HDR_SIZE + (off_t)f->head * (off_t)f->rec_size;
    if (pwrite(f->fd, rec, f->rec_size, off) != (ssize_t)f->rec_size) return -1;

    f->head = (f->head + 1) % (uint32_t)f->max_rec;
    if (f->count < (uint32_t)f->max_rec) f->count++;
    return write_header(f);
}

int trend_read_all(const char* dir, const char* id, trend_rec_t** out,
                   int* channels, int* interval_ms)
{
    char path[256];
    int fd, ch = 0, max_rec = 0;
    uint8_t h[TREND_HDR_SIZE];
    uint32_t head, count, iv = 0;
    size_t rec_size;
    uint8_t* buf = NULL;
    trend_rec_t* recs;
    int n;

    build_path(path, sizeof(path), dir, id);
    fd = open(path, O_RDONLY);
    if (fd < 0) return -1;
    if (pread(fd, h, sizeof(h), 0) != (ssize_t)sizeof(h) ||
        memcmp(h, TREND_MAGIC, 4) != 0) {
        close(fd);
        return -1;
    }
    ch = h[8];
    rec_size = (size_t)h[6] | ((size_t)h[7] << 8);
    iv = rd32(h + 12);
    max_rec = (int)rd32(h + 16);
    head = rd32(h + 20);
    count = rd32(h + 24);
    if (ch < 1 || ch > TREND_CH_MAX || rec_size < 8 || rec_size > 4 + 4 * TREND_CH_MAX) {
        close(fd);
        return -1;
    }
    if (count > (uint32_t)max_rec) count = (uint32_t)max_rec;

    recs = calloc(count ? count : 1, sizeof(*recs));
    if (!recs) {
        close(fd);
        return -1;
    }
    /* 环形转线性：最旧一条在 head-count 处 */
    for (uint32_t i = 0; i < count; i++) {
        uint32_t idx = (head + (uint32_t)max_rec - count + i) % (uint32_t)max_rec;
        off_t off = (off_t)TREND_HDR_SIZE + (off_t)idx * (off_t)rec_size;

        if (pread(fd, buf ? buf : (buf = malloc(rec_size)), rec_size, off) !=
            (ssize_t)rec_size) {
            continue;
        }
        recs[i].ts = rd32(buf);
        for (int k = 0; k < ch; k++)
            memcpy(&recs[i].v[k], buf + 4 + k * 4, 4);
    }
    free(buf);
    close(fd);

    n = (int)count;
    *out = recs;
    if (channels) *channels = ch;
    if (interval_ms) *interval_ms = (int)iv;
    return n;
}

int trend_stat(const char* dir, const char* id, int* count, int* channels,
               uint32_t* interval_ms)
{
    char path[256];
    int fd;
    uint8_t h[TREND_HDR_SIZE];

    build_path(path, sizeof(path), dir, id);
    fd = open(path, O_RDONLY);
    if (fd < 0) return -1;
    if (pread(fd, h, sizeof(h), 0) != (ssize_t)sizeof(h) ||
        memcmp(h, TREND_MAGIC, 4) != 0) {
        close(fd);
        return -1;
    }
    if (count) *count = (int)rd32(h + 24);
    if (channels) *channels = h[8];
    if (interval_ms) *interval_ms = rd32(h + 12);
    close(fd);
    return 0;
}
