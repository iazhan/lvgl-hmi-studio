/*
 * modbus.c — Modbus RTU 主站实现
 *
 * 每串口一个通讯线程（34 §5.4）：线程内按设备轮询、按 gap 合并读块；写请求由
 * 界面线程投进队列，线程在每个轮询间隙下发。LVGL 与字体缓存绝不在这里碰。
 *
 * 只做 v1 用到的功能码：01/02/03/04 读，05/06/16 写。异常码一律当失败处理
 * （该块内变量标 BAD），不做部分成功。
 */
#include "modbus.h"
#include "core/port_map.h"
#include "core/tagdb.h"
#include "hmi_log.h"

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

#define MB_QUEUE_DEPTH 16
#define MB_PORTS_MAX    8
#define MB_BLOCK_MAX   32
#define MB_POLL_SLICE_MS 20

typedef struct {
    int    dev;
    int    area, addr, datatype;
    double num;
    int    boolean;
    /* 字符串写（datatype = DT_STRING）：按 2 字节/寄存器铺，slen 是有效字节数 */
    char   str[65];
    int    slen;
} mb_wreq_t;

typedef struct {
    const project_t* proj;
    int   port_idx;
    char  node[64];
    int   fd;
    pthread_t tid;
    pthread_mutex_t lock;
    mb_wreq_t q[MB_QUEUE_DEPTH];
    int qh, qt, qn;
    volatile int run;

    /* 链路状态：日志只在状态变化时打一行，断线期间每 60 秒补一次心跳，
     * 否则 200ms 一次的轮询会把 /var/log（tmpfs）刷满 */
    uint8_t   link_ok[PROJ_DEV_MAX];
    long long link_beat[PROJ_DEV_MAX];
    long long fail_since;      /* 首次失败时刻，0 = 当前正常 */
    long long last_reopen;
    int       reopen_warned;   /* 重开失败只报一次，避免每 2 秒刷一行 */
} mb_port_t;

typedef struct {
    int area, start, count;    /* 线圈按位计，寄存器按 16 位计 */
} mb_block_t;

static mb_port_t s_ports[MB_PORTS_MAX];

/* 设备级启停（cg.stopDevice/startDevice）：只停读轮询，写照常下发 */
static uint8_t s_dev_off[PROJ_DEV_MAX];

int modbus_device_enable(const project_t* p, const char* dev_id, int on)
{
    if (!p || !dev_id) return -1;
    for (int i = 0; i < p->devices_count; i++) {
        if (strcmp(p->devices[i].id, dev_id) != 0) continue;
        s_dev_off[i] = on ? 0 : 1;
        HMI_LOG("modbus: 设备 %s %s", dev_id, on ? "恢复轮询" : "停止轮询");
        return 0;
    }
    return -1;
}
static int s_port_count;

/* ── 小工具 ── */

static long long now_ms(void)
{
    struct timespec ts;

    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static uint16_t crc16(const uint8_t* p, size_t n)
{
    uint16_t crc = 0xFFFF;

    for (size_t i = 0; i < n; i++) {
        crc ^= p[i];
        for (int b = 0; b < 8; b++)
            crc = (crc & 1) ? (uint16_t)((crc >> 1) ^ 0xA001) : (uint16_t)(crc >> 1);
    }
    return crc;
}

static int baud_const(int baud)
{
    switch (baud) {
    case 1200:   return B1200;
    case 2400:   return B2400;
    case 4800:   return B4800;
    case 9600:   return B9600;
    case 19200:  return B19200;
    case 38400:  return B38400;
    case 57600:  return B57600;
    case 115200: return B115200;
    default:     return 0;
    }
}

/* 寄存器宽度：线圈/离散输入按位算，数值按 16 位寄存器算 */
static int reg_width(const tag_def_t* t)
{
    switch (t->src_datatype) {
    case DT_BOOL:
    case DT_I16:
    case DT_U16:    return 1;
    case DT_I32:
    case DT_U32:
    case DT_F32:    return 2;
    case DT_STRING: {              /* ADR-0007：寄存器数 = ceil(len/2) */
        int n = t->src_len > 0 ? t->src_len : 1;

        return (n + 1) / 2;
    }
    default:        return 0;      /* i64/double 在加载期已被拒 */
    }
}

/* 取一个 16 位寄存器：BADC 时寄存器内高低字节互换（ADR-0007） */
static uint16_t word_at(const uint8_t* b, int off, int order)
{
    uint16_t w = (uint16_t)((b[off] << 8) | b[off + 1]);

    if (order == ORDER_BADC) w = (uint16_t)((w >> 8) | (w << 8));
    return w;
}

/* ── 串口 ── */

static int port_open(const char* node, const port_def_t* pc)
{
    struct termios tio;
    int fd, bs;

    bs = baud_const(pc->baud);
    if (bs == 0) {
        HMI_LOG("modbus: 波特率 %d 不支持", pc->baud);
        return -1;
    }
    fd = open(node, O_RDWR | O_NOCTTY | O_NONBLOCK);
    if (fd < 0) {
        HMI_LOG("modbus: 打不开 %s（%s）", node, strerror(errno));
        return -1;
    }
    if (tcgetattr(fd, &tio) != 0) {
        HMI_LOG("modbus: %s 读终端属性失败（%s）", node, strerror(errno));
        close(fd);
        return -1;
    }
    cfmakeraw(&tio);
    cfsetispeed(&tio, bs);
    cfsetospeed(&tio, bs);
    tio.c_cflag |= (CLOCAL | CREAD);
    tio.c_cflag &= ~CSIZE;
    tio.c_cflag |= (pc->data_bits == 7) ? CS7 : CS8;
    if (strcmp(pc->parity, "even") == 0) {
        tio.c_cflag |= PARENB;
        tio.c_cflag &= ~PARODD;
    } else if (strcmp(pc->parity, "odd") == 0) {
        tio.c_cflag |= (PARENB | PARODD);
    } else {
        tio.c_cflag &= ~PARENB;
    }
    if (pc->stop_bits == 2) tio.c_cflag |= CSTOPB;
    else tio.c_cflag &= ~CSTOPB;
    tio.c_cc[VMIN] = 0;
    tio.c_cc[VTIME] = 0;
    if (tcsetattr(fd, TCSANOW, &tio) != 0) {
        HMI_LOG("modbus: %s 设置 %d baud/%dN%s 失败（%s）", node, pc->baud,
                pc->data_bits, pc->parity, strerror(errno));
        close(fd);
        return -1;
    }
    tcflush(fd, TCIOFLUSH);
    return fd;
}

/* ── 帧收发 ── */

/* 等一帧回包：先按功能码算期望长度，收满即返回；超时或 CRC 错返回 -1 */
static int mb_recv(mb_port_t* mp, uint8_t* buf, int bufsz, int timeout_ms)
{
    long long t0 = now_ms();
    int got = 0, want = 0;

    while (now_ms() - t0 < timeout_ms) {
        struct pollfd pfd;
        int pr, r;

        pfd.fd = mp->fd;
        pfd.events = POLLIN;
        pfd.revents = 0;
        pr = poll(&pfd, 1, 10);
        if (pr <= 0) continue;
        r = (int)read(mp->fd, buf + got, (size_t)(bufsz - got));
        if (r <= 0) continue;
        got += r;

        if (want == 0 && got >= 2) {
            uint8_t fc = buf[1];
            if (fc & 0x80) want = 5;
            else if (fc >= 1 && fc <= 4) {
                if (got >= 3) want = 3 + buf[2] + 2;
            } else want = 8;
        }
        if (want && got >= want) break;
    }
    if (got < 5) return -1;
    {
        uint16_t crc = crc16(buf, (size_t)(got - 2));
        uint16_t got_crc = (uint16_t)(buf[got - 2] | (buf[got - 1] << 8));
        if (crc != got_crc) return -1;
    }
    return got;
}

/* 发一帧并收回包（不含 CRC 的两字节已去掉）；异常码返回 -1 */
static int mb_transact(mb_port_t* mp, const uint8_t* req, int reqlen,
                       uint8_t* resp, int respsz, int timeout_ms)
{
    uint8_t frame[64];
    uint16_t crc;
    int n, got;

    if (reqlen + 2 > (int)sizeof(frame)) return -1;
    memcpy(frame, req, (size_t)reqlen);
    crc = crc16(frame, (size_t)reqlen);
    frame[reqlen] = (uint8_t)(crc & 0xFF);
    frame[reqlen + 1] = (uint8_t)(crc >> 8);
    n = reqlen + 2;

    tcflush(mp->fd, TCIFLUSH);
    if (write(mp->fd, frame, (size_t)n) != n) return -1;

    got = mb_recv(mp, resp, respsz, timeout_ms);
    if (got < 0) return -1;
    if (resp[1] & 0x80) {
        HMI_LOG("modbus: 从站异常码 %d（fc %d）", resp[2], resp[1] & 0x7F);
        return -1;
    }
    return got - 2;             /* 去掉 CRC */
}

/* 读一段：fc 1/2/3/4；成功时 out 里是数据字节（线圈是位图，寄存器是 16 位大端） */
static int mb_read(mb_port_t* mp, int slave, int fc, int addr, int count,
                   uint8_t* out, int outsz, int* outlen, int timeout_ms)
{
    uint8_t req[6];
    uint8_t resp[300];

    if (count <= 0 || count > 2000) return -1;
    req[0] = (uint8_t)slave;
    req[1] = (uint8_t)fc;
    req[2] = (uint8_t)(addr >> 8);
    req[3] = (uint8_t)(addr & 0xFF);
    req[4] = (uint8_t)(count >> 8);
    req[5] = (uint8_t)(count & 0xFF);
    if (mb_transact(mp, req, 6, resp, (int)sizeof(resp), timeout_ms) < 0) return -1;
    if (resp[1] != (uint8_t)fc) return -1;
    if (resp[2] > outsz) return -1;
    memcpy(out, resp + 3, resp[2]);
    *outlen = resp[2];
    return 0;
}

static int mb_write_single(mb_port_t* mp, int slave, int fc, int addr, int value,
                           int timeout_ms)
{
    uint8_t req[6];
    uint8_t resp[16];

    req[0] = (uint8_t)slave;
    req[1] = (uint8_t)fc;
    req[2] = (uint8_t)(addr >> 8);
    req[3] = (uint8_t)(addr & 0xFF);
    req[4] = (uint8_t)(value >> 8);
    req[5] = (uint8_t)(value & 0xFF);
    if (mb_transact(mp, req, 6, resp, (int)sizeof(resp), timeout_ms) < 0) return -1;
    /* 回包是请求的回显，逐字节比对能抓到串口上的错帧 */
    if (memcmp(req, resp, 6) != 0) return -1;
    return 0;
}

static int mb_write_regs(mb_port_t* mp, int slave, int addr, int count,
                         const uint16_t* vals, int timeout_ms)
{
    uint8_t req[8 + 2 * 32];
    uint8_t resp[16];
    int n = 0;

    if (count <= 0 || count > 32) return -1;
    req[n++] = (uint8_t)slave;
    req[n++] = 0x10;
    req[n++] = (uint8_t)(addr >> 8);
    req[n++] = (uint8_t)(addr & 0xFF);
    req[n++] = (uint8_t)(count >> 8);
    req[n++] = (uint8_t)(count & 0xFF);
    req[n++] = (uint8_t)(count * 2);
    for (int i = 0; i < count; i++) {
        req[n++] = (uint8_t)(vals[i] >> 8);
        req[n++] = (uint8_t)(vals[i] & 0xFF);
    }
    if (mb_transact(mp, req, n, resp, (int)sizeof(resp), timeout_ms) < 0) return -1;
    if (resp[1] != 0x10) return -1;
    if ((resp[2] != req[2]) || (resp[3] != req[3]) ||
        (resp[4] != req[4]) || (resp[5] != req[5])) return -1;
    return 0;
}

/* ── 编解码 ── */

/* 寄存器字节 → 工程值。order=CDAB 时 32 位数据的两个字互换（低字在前）。 */
static double regs_to_num(const uint8_t* b, int off, int datatype, int order)
{
    switch (datatype) {
    case DT_BOOL:
    case DT_I16:  return (double)(int16_t)word_at(b, off, order);
    case DT_U16:  return (double)word_at(b, off, order);
    case DT_I32:
    case DT_U32:
    case DT_F32: {
        uint16_t w0 = word_at(b, off, order);
        uint16_t w1 = word_at(b, off + 2, order);
        uint32_t u;

        if (order == ORDER_CDAB) u = ((uint32_t)w1 << 16) | w0;
        else u = ((uint32_t)w0 << 16) | w1;
        if (datatype == DT_I32) return (double)(int32_t)u;
        if (datatype == DT_U32) return (double)u;
        {
            float f;
            memcpy(&f, &u, sizeof(f));
            return (double)f;
        }
    }
    default:
        return 0.0;
    }
}

/* 工程值 → 寄存器字（写路径用）；返回写入的字数，0 表示不支持 */
static int num_to_regs(double v, int datatype, int order, uint16_t* out)
{
    switch (datatype) {
    case DT_BOOL:
    case DT_I16:  out[0] = (uint16_t)(int16_t)v; return 1;
    case DT_U16:  out[0] = (uint16_t)v; return 1;
    case DT_I32:
    case DT_U32:
    case DT_F32: {
        uint32_t u;
        uint16_t hi, lo;

        if (datatype == DT_F32) {
            float f = (float)v;
            memcpy(&u, &f, sizeof(u));
        } else if (datatype == DT_I32) {
            u = (uint32_t)(int32_t)v;
        } else {
            u = (uint32_t)v;
        }
        hi = (uint16_t)(u >> 16);
        lo = (uint16_t)(u & 0xFFFF);
        if (order == ORDER_CDAB) { out[0] = lo; out[1] = hi; }
        else { out[0] = hi; out[1] = lo; }
        return 2;
    }
    default:
        return 0;
    }
}

/* ── 读块规划 ── */

typedef struct {
    int tag;        /* project 的 tag 下标 */
    int area, addr, width;
} mb_item_t;

static int item_cmp(const void* a, const void* b)
{
    const mb_item_t* x = a;
    const mb_item_t* y = b;

    if (x->area != y->area) return x->area - y->area;
    return x->addr - y->addr;
}

/* 把某设备的变量按 (area, addr) 排序后按 gap 合并；超出单帧上限就切块。
 * 返回块数，0 表示该设备没有可读变量。 */
static int plan_blocks(const project_t* p, int dev, int gap,
                       mb_item_t* items, int max_items,
                       mb_block_t* blocks, int max_blocks, int* n_items)
{
    int n = 0, nb = 0;

    for (int i = 0; i < p->tags_count && n < max_items; i++) {
        const tag_def_t* t = &p->tags[i];
        int w;

        if (!t->has_source || t->src_dev_sim) continue;
        if (strcmp(t->src_device, p->devices[dev].id) != 0) continue;
        w = reg_width(t);
        if (w <= 0) continue;
        items[n].tag = i;
        items[n].area = t->src_area;
        items[n].addr = t->src_addr;
        items[n].width = w;
        n++;
    }
    *n_items = n;
    if (n == 0) return 0;
    qsort(items, (size_t)n, sizeof(items[0]), item_cmp);

    for (int i = 0; i < n && nb < max_blocks; i++) {
        int start = items[i].addr;
        int end = start + items[i].width;
        int area = items[i].area;
        int limit = (area == AREA_COIL || area == AREA_DISCRETE) ? 2000 : 125;

        while (i + 1 < n && items[i + 1].area == area &&
               items[i + 1].addr <= end + gap &&
               items[i + 1].addr + items[i + 1].width - start <= limit) {
            i++;
            end = items[i].addr + items[i].width;
        }
        blocks[nb].area = area;
        blocks[nb].start = start;
        blocks[nb].count = end - start;
        nb++;
    }
    return nb;
}

static int area_fc(int area)
{
    switch (area) {
    case AREA_COIL:     return 1;
    case AREA_DISCRETE: return 2;
    case AREA_INPUT:    return 4;
    default:            return 3;
    }
}

/* ── 轮询一个设备 ── */

static void poll_device(mb_port_t* mp, int dev, mb_item_t* items,
                        mb_block_t* blocks)
{
    const project_t* p = mp->proj;
    const device_def_t* d = &p->devices[dev];
    uint8_t buf[300];
    int nb, n_items = 0, dev_ok = 1;

    nb = plan_blocks(p, dev, d->gap, items, PROJ_TAGS_MAX, blocks, MB_BLOCK_MAX,
                     &n_items);
    for (int b = 0; b < nb; b++) {
        int outlen = 0, ok = 0, fc = area_fc(blocks[b].area);

        for (int try_ = 0; try_ <= d->retries && !ok; try_++)
            ok = (mb_read(mp, d->slave, fc, blocks[b].start, blocks[b].count,
                          buf, (int)sizeof(buf), &outlen, d->timeout_ms) == 0);
        if (!ok) dev_ok = 0;
        for (int i = 0; i < n_items; i++) {
            const tag_def_t* t;
            int off;

            if (items[i].area != blocks[b].area) continue;
            if (items[i].addr < blocks[b].start ||
                items[i].addr + items[i].width > blocks[b].start + blocks[b].count)
                continue;
            t = &p->tags[items[i].tag];
            if (!ok) {
                tagdb_set_num(items[i].tag, 0.0, TAG_Q_BAD);
                continue;
            }
            off = items[i].addr - blocks[b].start;
            if (blocks[b].area == AREA_COIL || blocks[b].area == AREA_DISCRETE) {
                int bit = (buf[off / 8] >> (off % 8)) & 1;
                tagdb_set_bool(items[i].tag, bit, TAG_Q_GOOD);
            } else if (t->src_datatype == DT_STRING) {
                char tmp[65];
                int cap = t->src_len > 0 ? t->src_len : 1;
                int n = 0, k;

                if (cap > 64) cap = 64;
                if (off * 2 + cap > blocks[b].count * 2)
                    cap = blocks[b].count * 2 - off * 2;
                for (k = 0; k < cap; k++) {
                    int kk = (d->order == ORDER_BADC) ? (k ^ 1) : k;

                    tmp[k] = (char)buf[off * 2 + kk];
                }
                for (k = 0; k < cap; k++)
                    if (tmp[k] == '\0') break;    /* 设备常用 0 补齐，到 0 为止 */
                n = k;
                tagdb_set_str(items[i].tag, tmp, n, TAG_Q_GOOD);
            } else {
                double v = regs_to_num(buf, off * 2, t->src_datatype, d->order);
                tagdb_set_num(items[i].tag, v, TAG_Q_GOOD);
            }
        }
    }

    /* 链路状态变化才打日志；断线期间每 60 秒补一行，看得出断了多久 */
    if (dev_ok) {
        if (!mp->link_ok[dev]) HMI_LOG("modbus: %s 恢复", d->id);
        mp->link_ok[dev] = 1;
        mp->fail_since = 0;
    } else {
        long long t = now_ms();

        if (mp->fail_since == 0) mp->fail_since = t;
        if (mp->link_ok[dev] || t - mp->link_beat[dev] > 60000) {
            HMI_LOG("modbus: %s 读失败（重试 %d 次仍无响应）", d->id, d->retries);
            mp->link_beat[dev] = t;
        }
        mp->link_ok[dev] = 0;
    }
}

/* ── 写请求 ── */

static void do_write(mb_port_t* mp, const mb_wreq_t* w)
{
    const project_t* p = mp->proj;
    const device_def_t* d = &p->devices[w->dev];
    uint16_t regs[2];
    int ok = 0, cnt;

    if (w->area == AREA_COIL) {
        for (int try_ = 0; try_ <= d->retries && !ok; try_++)
            ok = (mb_write_single(mp, d->slave, 5, w->addr,
                                  w->boolean ? 0xFF00 : 0x0000, d->timeout_ms) == 0);
    } else if (w->datatype == DT_BOOL) {
        for (int try_ = 0; try_ <= d->retries && !ok; try_++)
            ok = (mb_write_single(mp, d->slave, 6, w->addr,
                                  w->boolean ? 1 : 0, d->timeout_ms) == 0);
    } else if (w->datatype == DT_STRING) {
        /* 与读回对称：字节序按设备的 order 摆，末尾补 0（设备侧按 0 截断） */
        uint16_t sregs[33];
        int nb = (w->slen > 64) ? 64 : w->slen;
        int cnt = (nb + 1) / 2;

        if (cnt < 1) cnt = 1;
        memset(sregs, 0, sizeof(sregs));
        for (int k = 0; k < cnt * 2; k++) {
            uint8_t b = (k < nb) ? (uint8_t)w->str[k] : 0;
            int kk = (d->order == ORDER_BADC) ? (k ^ 1) : k;

            if (kk & 1) sregs[kk / 2] |= b;
            else sregs[kk / 2] |= (uint16_t)b << 8;
        }
        for (int try_ = 0; try_ <= d->retries && !ok; try_++)
            ok = (mb_write_regs(mp, d->slave, w->addr, cnt, sregs, d->timeout_ms) == 0);
    } else {
        cnt = num_to_regs(w->num, w->datatype, d->order, regs);
        if (cnt == 1) {
            for (int try_ = 0; try_ <= d->retries && !ok; try_++)
                ok = (mb_write_single(mp, d->slave, 6, w->addr, regs[0],
                                      d->timeout_ms) == 0);
        } else if (cnt == 2) {
            for (int try_ = 0; try_ <= d->retries && !ok; try_++)
                ok = (mb_write_regs(mp, d->slave, w->addr, 2, regs,
                                    d->timeout_ms) == 0);
        }
    }
    if (!ok) HMI_LOG("modbus: %s 写 tag 失败 area=%d addr=%d", d->id, w->area, w->addr);
}

static void drain_writes(mb_port_t* mp)
{
    for (;;) {
        mb_wreq_t w;

        pthread_mutex_lock(&mp->lock);
        if (mp->qn == 0) {
            pthread_mutex_unlock(&mp->lock);
            return;
        }
        w = mp->q[mp->qh];
        mp->qh = (mp->qh + 1) % MB_QUEUE_DEPTH;
        mp->qn--;
        pthread_mutex_unlock(&mp->lock);
        do_write(mp, &w);
    }
}

/* ── 线程 ── */

static void* port_thread(void* arg)
{
    mb_port_t* mp = arg;
    const project_t* p = mp->proj;
    const char* pid = p->ports[mp->port_idx].id;
    long long next[PROJ_DEV_MAX];
    /* 读块规划用的两块临时区：放线程栈上，避免每次轮询 malloc */
    mb_item_t* items = calloc(PROJ_TAGS_MAX, sizeof(*items));
    mb_block_t* blocks = calloc(MB_BLOCK_MAX, sizeof(*blocks));

    if (!items || !blocks) {
        HMI_LOG("modbus: 读块规划缓冲区分配失败（%s）", pid);
        free(items);
        free(blocks);
        return NULL;
    }
    for (int i = 0; i < p->devices_count; i++) next[i] = 0;

    while (mp->run) {
        long long now;

        drain_writes(mp);
        now = now_ms();
        for (int i = 0; i < p->devices_count; i++) {
            const device_def_t* d = &p->devices[i];

            if (strcmp(d->protocol, "modbus_rtu") != 0) continue;
            if (strcmp(d->port, pid) != 0) continue;
            if (now < next[i]) continue;
            if (s_dev_off[i]) { next[i] = now + d->poll_ms; continue; }
            poll_device(mp, i, items, blocks);
            next[i] = now_ms() + d->poll_ms;
        }
        /* USB 转串口被拔掉时 fd 会一直报 EIO：连续失败超过 2 秒就重开一次，
         * 插回来能自己恢复，不用重启程序 */
        if (mp->fail_since && now - mp->fail_since > 2000 &&
            now - mp->last_reopen > 2000) {
            mp->last_reopen = now;
            close(mp->fd);
            mp->fd = port_open(mp->node, &p->ports[mp->port_idx]);
            for (int i = 0; i < p->devices_count; i++) mp->link_ok[i] = 0;
            if (mp->fd < 0) {
                if (!mp->reopen_warned) {
                    HMI_LOG("modbus: %s 重新打开失败（每 2 秒重试，恢复后自动继续）",
                            mp->node);
                    mp->reopen_warned = 1;
                }
            } else if (mp->reopen_warned) {
                HMI_LOG("modbus: %s 重新打开成功", mp->node);
                mp->reopen_warned = 0;
            }
        }
        {
            struct timespec ts = { 0, MB_POLL_SLICE_MS * 1000000L };
            nanosleep(&ts, NULL);
        }
    }
    free(items);
    free(blocks);
    return NULL;
}

/* ── 对外入口 ── */

int modbus_start(const project_t* p)
{
    int started = 0;

    for (int pi = 0; pi < p->ports_count; pi++) {
        const char* pid = p->ports[pi].id;
        mb_port_t* mp;
        int used = 0;


        for (int di = 0; di < p->devices_count; di++)
            if (strcmp(p->devices[di].protocol, "modbus_rtu") == 0 &&
                strcmp(p->devices[di].port, pid) == 0) used = 1;
        if (!used) {
            HMI_LOG("modbus: 串口 %s 没有 modbus_rtu 设备，跳过", pid);
            continue;
        }
        if (s_port_count >= MB_PORTS_MAX) {
            HMI_LOG("modbus: 串口数超过上限 %d", MB_PORTS_MAX);
            break;
        }
        mp = &s_ports[s_port_count];
        memset(mp, 0, sizeof(*mp));
        mp->proj = p;
        mp->port_idx = pi;
        {
            const char* node = port_node(pid);

            if (!node) {
                HMI_LOG("modbus: 逻辑串口 %s 没有设备节点映射", pid);
                continue;
            }
            snprintf(mp->node, sizeof(mp->node), "%s", node);
        }
        mp->fd = port_open(mp->node, &p->ports[pi]);
        if (mp->fd < 0) continue;           /* 打不开就只影响这一个口 */
        pthread_mutex_init(&mp->lock, NULL);
        mp->run = 1;
        if (pthread_create(&mp->tid, NULL, port_thread, mp) != 0) {
            HMI_LOG("modbus: 串口 %s 线程创建失败", pid);
            close(mp->fd);
            mp->fd = -1;
            pthread_mutex_destroy(&mp->lock);
            continue;
        }
        s_port_count++;
        started++;
        HMI_LOG("modbus: %s → %s，%d baud，线程已起", pid, mp->node,
                p->ports[pi].baud);
    }
    return started;
}

static int dev_index(const project_t* p, const char* id)
{
    for (int i = 0; i < p->devices_count; i++)
        if (strcmp(p->devices[i].id, id) == 0) return i;
    return -1;
}

/* 找到设备所在串口的上下文；设备不存在或不是 modbus_rtu 返回 NULL */
static mb_port_t* port_of_dev(const project_t* p, int di)
{
    if (di < 0 || di >= p->devices_count) return NULL;
    if (strcmp(p->devices[di].protocol, "modbus_rtu") != 0) return NULL;
    for (int pi = 0; pi < s_port_count; pi++)
        if (strcmp(p->ports[pi].id, p->devices[di].port) == 0) return &s_ports[pi];
    return NULL;
}

/* 入队。队列满返回 -1（37：满了拒绝并提示，不阻塞界面） */
static int enqueue(mb_port_t* mp, const mb_wreq_t* w)
{
    int rc = 0;

    pthread_mutex_lock(&mp->lock);
    if (mp->qn >= MB_QUEUE_DEPTH) {
        rc = -1;
    } else {
        mp->q[mp->qt] = *w;
        mp->qt = (mp->qt + 1) % MB_QUEUE_DEPTH;
        mp->qn++;
    }
    pthread_mutex_unlock(&mp->lock);
    return rc;
}

int modbus_device_online(const project_t* p, const char* dev_id)
{
    int di;
    mb_port_t* mp;
    const char* proto;

    if (!p || !dev_id) return -1;
    di = dev_index(p, dev_id);
    if (di < 0) return -1;
    proto = project_device_protocol(p, dev_id);
    if (proto && strcmp(proto, "sim") == 0) return 1;   /* 仿真设备不会断线 */
    mp = port_of_dev(p, di);
    /* 端口根本没起来（串口打不开、设备未启用）也算链路不通——现场断线就是这种表现 */
    if (!mp) return 0;
    return mp->link_ok[di] ? 1 : 0;
}

/* 字符串写：投一个 DT_STRING 的写请求，由串口线程按 fc16 下发 */
int modbus_write_tag_str(const project_t* p, int tag_id, const char* s, int n)
{
    const tag_def_t* t;
    mb_port_t* mp;
    mb_wreq_t w;
    int di;

    if (!s || tag_id < 0 || tag_id >= p->tags_count) return -1;
    t = &p->tags[tag_id];
    if (!t->has_source || t->src_dev_sim) return 0;
    di = dev_index(p, t->src_device);
    mp = port_of_dev(p, di);
    if (!mp) return 0;

    memset(&w, 0, sizeof(w));
    w.dev = di;
    w.area = t->src_area;
    w.addr = t->src_addr;
    w.datatype = DT_STRING;
    if (n > (int)sizeof(w.str) - 1) n = (int)sizeof(w.str) - 1;
    if (t->src_len > 0 && n > t->src_len) n = t->src_len;
    memcpy(w.str, s, (size_t)n);
    w.slen = n;
    if (enqueue(mp, &w) != 0) {
        HMI_LOG("modbus: 写队列满（%d），tag%d 的字符串写被拒绝", MB_QUEUE_DEPTH, tag_id);
        return -1;
    }
    return 0;
}

int modbus_write_tag(const project_t* p, int tag_id, double num, int boolean)
{
    const tag_def_t* t;
    mb_port_t* mp;
    mb_wreq_t w;
    int di;

    if (tag_id < 0 || tag_id >= p->tags_count) return -1;
    t = &p->tags[tag_id];
    if (!t->has_source || t->src_dev_sim) return 0;
    di = dev_index(p, t->src_device);
    mp = port_of_dev(p, di);
    if (!mp) return 0;

    memset(&w, 0, sizeof(w));
    w.dev = di;
    w.area = t->src_area;
    w.addr = t->src_addr;
    w.datatype = t->src_datatype;
    w.num = num;
    w.boolean = boolean;
    if (enqueue(mp, &w) != 0) {
        HMI_LOG("modbus: 写队列满（%d），tag%d 的写被拒绝", MB_QUEUE_DEPTH, tag_id);
        return -1;
    }
    return 0;
}

int modbus_write_device(const project_t* p, const char* dev_id, int area, int addr,
                        int datatype, double num, int boolean)
{
    mb_port_t* mp;
    mb_wreq_t w;
    int di = dev_index(p, dev_id);

    mp = port_of_dev(p, di);
    if (!mp) return -1;

    memset(&w, 0, sizeof(w));
    w.dev = di;
    w.area = area;
    w.addr = addr;
    w.datatype = datatype;
    w.num = num;
    w.boolean = boolean;
    if (enqueue(mp, &w) != 0) {
        HMI_LOG("modbus: 写队列满（%d），设备 %s 的写被拒绝", MB_QUEUE_DEPTH, dev_id);
        return -1;
    }
    return 0;
}

void modbus_stop(void)
{
    for (int i = 0; i < s_port_count; i++) {
        s_ports[i].run = 0;
        pthread_join(s_ports[i].tid, NULL);
        if (s_ports[i].fd >= 0) close(s_ports[i].fd);
        pthread_mutex_destroy(&s_ports[i].lock);
    }
    s_port_count = 0;
}
