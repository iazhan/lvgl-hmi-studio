/*
 * chenge-preview-host —— 隔离预览宿主（独立进程）
 *
 * 为什么要有它：预览里会跑用户写的 Lua，死循环/越界/原生接口 bug 都可能发生。
 * 进程内加载 .so 会把这些后果直接甩给编辑器（未保存的工程一起没）。所以编辑器
 * 默认走这条：**同一份渲染代码**放进子进程，编辑器只通过共享内存拿帧、通过
 * 标准输入发指针。
 *
 * 与进程内版（libchenge_runtime.so）的关系：两者链接的是同一批 core/ui/project
 * 目标文件，只有平台后端与"帧往哪去"不同 —— 预览等于实机的根基在这里。
 *
 * 通道：
 *   帧   共享内存（名字由 --shm 给）。头 40 字节 + 整屏 ARGB8888；
 *        宿主每帧拷一次并 seq++，客户端只读。
 *   输入 标准输入，一行一条：`P x y down`；`Q` 退出。
 *   日志 标准错误（编辑器按行转发到输出面板）。
 *   父进程死 → 标准输入 EOF → 本进程自行退出，不留孤儿。
 */
#include "chenge_embed.h"
#include "hmi_log.h"
#include "lvgl.h"          /* --bench 直接改单个 lv_obj 的几何，量「补丁 → 新帧」 */

#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>
#ifndef _WIN32
#include <sys/mman.h>
#endif

#define FRAME_MAGIC   0x46474843u   /* 'CHGF' */
#define FRAME_VERSION 2u

/* 头布局必须与编辑器侧 ChengeProcessSession 逐字段一致。
 * 像素起点由 header_bytes 给出，不写死 sizeof —— 以后再加字段，
 * 客户端按它定位像素就不会错位（版本 1 时这里没有这一项，两侧曾按 32/40 各算一套）。 */
typedef struct {
    uint32_t magic;
    uint32_t version;
    uint32_t w;
    uint32_t h;
    uint32_t stride;
    uint32_t header_bytes;      /* 像素起点 = sizeof(frame_hdr_t) */
    volatile uint64_t seq;      /* 每拷完一帧 +1；客户端只读 */
    uint32_t applied_epoch;     /* 已应用到这一帧的页面代（补丁通道用） */
    uint32_t applied_seq;       /* 该 epoch 内已应用到的补丁序号 */
} frame_hdr_t;

/* 帧通道的落点（publish_frame 与命令处理共用一个入口） */
static frame_hdr_t* g_hdr;
static void*        g_base;
static int          g_w, g_h;

/*
 * 帧通道的两种实现（这是本文件唯一"平台机制不同"的地方，所以留 #ifdef）：
 *   Linux  —— POSIX 共享内存：/dev/shm/<name>，客户端按路径 mmap。
 *   Windows —— 命名文件映射：名字 = shm_name 去掉前导 '/'，客户端用
 *              MemoryMappedFile.OpenExisting("<同名>") 打开。头布局与像素格式
 *              完全一致，所以协议层不用分叉。
 */
#ifndef _WIN32
static int s_shm_fd = -1;

static void* shm_map_create(const char* name, size_t len)
{
    void* p;

    s_shm_fd = shm_open(name, O_CREAT | O_RDWR, 0600);
    if (s_shm_fd < 0) {
        perror("shm_open");
        return NULL;
    }
    if (ftruncate(s_shm_fd, (off_t)len) != 0) {
        perror("ftruncate");
        return NULL;
    }
    p = mmap(NULL, len, PROT_READ | PROT_WRITE, MAP_SHARED, s_shm_fd, 0);
    if (p == MAP_FAILED) {
        perror("mmap");
        return NULL;
    }
    return p;
}

static void shm_map_destroy(void* base, size_t len)
{
    if (base) munmap(base, len);
    if (s_shm_fd >= 0) {
        close(s_shm_fd);
        s_shm_fd = -1;
    }
}
#else
static HANDLE s_shm_map;

static void* shm_map_create(const char* name, size_t len)
{
    char plain[256];
    const char* p = name;

    while (*p == '/') p++;              /* Windows 的对象名不带路径分隔 */
    snprintf(plain, sizeof(plain), "%s", p);
    s_shm_map = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE,
                                   (DWORD)((unsigned long long)len >> 32),
                                   (DWORD)(len & 0xFFFFFFFFull), plain);
    if (!s_shm_map) {
        fprintf(stderr, "CreateFileMapping 失败（%s，err=%lu）\n", plain, GetLastError());
        return NULL;
    }
    return MapViewOfFile(s_shm_map, FILE_MAP_ALL_ACCESS, 0, 0, len);
}

static void shm_map_destroy(void* base, size_t len)
{
    (void)len;
    if (base) UnmapViewOfFile(base);
    if (s_shm_map) {
        CloseHandle(s_shm_map);
        s_shm_map = NULL;
    }
}
#endif

static volatile sig_atomic_t s_quit;

static void on_signal(int sig)
{
    (void)sig;
    s_quit = 1;
}

/*
 * 从标准输入读工程（`--project-stdin`）：编辑器把导出的文件直接推过来，
 * 不必先写临时目录 —— 隔离与"不落盘"两个目标才能同时成立。
 *
 * 帧格式（与 ChengeProcessSession 一致，全是二进制安全的分块）：
 *     F <相对路径> <字节数>\n<字节数 个字节>\n
 *     ...
 *     E\n
 */
static int read_project_blob(void)
{
    char line[512];

    chenge_embed_project_clear();
    while (fgets(line, sizeof(line), stdin)) {
        char rel[400];
        long len;

        if (line[0] == 'E') return 0;
        if (line[0] != 'F') continue;
        if (sscanf(line + 1, " %399s %ld", rel, &len) != 2 || len <= 0 || len > 8 * 1024 * 1024)
            return -1;
        {
            char* buf = (char*)malloc((size_t)len);

            if (!buf) return -1;
            if (fread(buf, 1, (size_t)len, stdin) != (size_t)len) {
                free(buf);
                return -1;
            }
            if (chenge_embed_project_put(rel, buf, (int)len) != 0) {
                free(buf);
                return -1;
            }
            free(buf);
        }
        (void)fgetc(stdin);          /* 吃掉数据后的换行 */
    }
    return -1;
}

/* ── --bench：S0 的实测入口（docs/chenge-rt/49 §4）──
 *
 * 量三件事：① 打开工程到首帧；② 单条几何补丁（lv_obj_set_pos）→ 新帧；
 * ③ 连续 N 条补丁的吞吐与抖动。每拍都按真实通道整屏拷贝一次 ARGB8888 ——
 * 只量渲染不量搬运会得到"乐观值"，而拖动跟手恰恰被拷贝吃掉。
 *
 * 注意：这里用 lv_obj_set_pos 直接改 obj，绕过页面层的 widget_t；
 * 这正是设计画布拖动补丁的快路径（几何不重建控件）。
 */
static uint64_t bench_now_us(void)
{
    struct timespec ts;

    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000ull + (uint64_t)ts.tv_nsec / 1000ull;
}

static void bench_run(int n)
{
    int w = 0, h = 0, stride = 0;
    const void* px;
    void* dst;
    lv_obj_t* obj;
    uint64_t t0, t1, total = 0, lo = UINT64_MAX, hi = 0;
    int base_x, base_y;

    t0 = bench_now_us();
    chenge_embed_tick();
    px = chenge_embed_frame(&w, &h, &stride);
    t1 = bench_now_us();
    fprintf(stderr, "BENCH first_frame_us=%llu %dx%d stride=%d frame=%p\n",
            (unsigned long long)(t1 - t0), w, h, stride, px);
    if (!px || stride <= 0 || h <= 0) return;

    obj = lv_obj_get_child(lv_screen_active(), 0);
    if (!obj) {
        fprintf(stderr, "BENCH 起始页没有可动的控件（用带控件的页面）\n");
        return;
    }
    dst = malloc((size_t)stride * (size_t)h);
    if (!dst) return;

    base_x = (int)lv_obj_get_x(obj);
    base_y = (int)lv_obj_get_y(obj);

    for (int i = 0; i < n; i++) {
        t0 = bench_now_us();
        lv_obj_set_pos(obj, base_x + i % 8, base_y + i % 5);
        chenge_embed_tick();
        px = chenge_embed_frame(&w, &h, &stride);
        if (px) memcpy(dst, px, (size_t)stride * (size_t)h);
        t1 = bench_now_us();
        {
            uint64_t d = t1 - t0;

            total += d;
            if (d < lo) lo = d;
            if (d > hi) hi = d;
        }
    }
    lv_obj_set_pos(obj, base_x, base_y);
    chenge_embed_tick();

    fprintf(stderr,
            "BENCH geom_patch n=%d avg_us=%llu min_us=%llu max_us=%llu fps=%.1f copy_bytes=%d\n",
            n, (unsigned long long)(total / (uint64_t)n), (unsigned long long)lo,
            (unsigned long long)hi, (double)n * 1e6 / (double)total, stride * h);
    free(dst);
}

/* 把当前帧拷到共享内存并 seq++。渲染（tick 或 render_once）之后调用；
 * 客户端按 seq 变化判断"有新帧"，所以没渲染就不要调。 */
/* 上一帧的像素（判"这一帧到底有没有变"）。只有真的重画过才 +seq：
 *
 * 主循环是 16ms 一拍、每拍无条件 publish，而画面只在 LVGL 真的重绘时才变
 * （管道的流动定时器 40ms 一拍 ⇒ 25 次/秒）。不判差异的话，客户端会被
 * "seq 变了"骗着每秒整屏拷贝 + 整窗重绘 60 次，其中 35 次是同一张图 ——
 * 实时预览里就是"管道自己在动时帧率反而低"（UI 线程被无谓的重绘吃掉）。
 * memcmp 1.5MB 只要 ~0.2ms，比一次贴图+重绘便宜两个数量级。 */
static uint8_t* s_last_px;
static size_t   s_last_len;

static void publish_frame_force(int force)
{
    int fw = 0, fh = 0, fstride = 0;
    const void* px;
    size_t len;

    if (!g_hdr || !g_base) return;
    px = chenge_embed_frame(&fw, &fh, &fstride);
    if (!px || fw != g_w || fh != g_h) return;

    len = (size_t)fstride * (size_t)fh;
    if (!force && s_last_px && s_last_len == len && memcmp(s_last_px, px, len) == 0) return;

    if (s_last_len != len) {
        free(s_last_px);
        s_last_px = (uint8_t*)malloc(len);
        s_last_len = s_last_px ? len : 0;
    }
    if (s_last_px) memcpy(s_last_px, px, len);

    memcpy((uint8_t*)g_base + sizeof(frame_hdr_t), px, len);
    g_hdr->seq++;
}

int main(int argc, char** argv)
{
    const char* project = NULL;
    const char* data_dir = NULL;
    const char* shm_name = NULL;
    const char* shot = NULL;
    int from_stdin = 0;
    int max_frames = 0;          /* 0 = 不限（调试时用 --frames 让进程自己退） */
    int bench_n = 0;             /* >0 = 只跑 --bench 测得退出，不开共享内存 */
    int design = 0;              /* 1 = 设计模式：不推进时间，只在命令驱动下重绘 */
    char err[256] = { 0 };
    void* base = NULL;
    size_t map_len = 0;
    frame_hdr_t* hdr;
    int w = 0, h = 0;
    int rc;

#ifdef _WIN32
    hmi_win_set_binary_stdio();
#endif

    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--project") && i + 1 < argc) project = argv[++i];
        else if (!strcmp(argv[i], "--project-stdin")) from_stdin = 1;
        else if (!strcmp(argv[i], "--data") && i + 1 < argc) data_dir = argv[++i];
        else if (!strcmp(argv[i], "--shm") && i + 1 < argc) shm_name = argv[++i];
        else if (!strcmp(argv[i], "--shot") && i + 1 < argc) shot = argv[++i];
        else if (!strcmp(argv[i], "--frames") && i + 1 < argc) max_frames = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--bench") && i + 1 < argc) bench_n = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--design")) design = 1;
        else {
            fprintf(stderr, "用法：%s --project <目录> --shm <名字> [--data <目录>] [--design] [--frames N] [--shot a.bmp]\n"
                            "       %s --project <目录> --bench N   （只测补丁耗时，不建共享内存）\n",
                    argv[0], argv[0]);
            return 64;
        }
    }
    if ((!shm_name && bench_n <= 0) || (!project && !from_stdin) || (from_stdin && !data_dir)) {
        fprintf(stderr, "缺 --shm（或 --bench N），或缺 --project / --project-stdin（后者要 --data）\n");
        return 64;
    }

    signal(SIGTERM, on_signal);
    signal(SIGINT, on_signal);
#ifndef _WIN32
    signal(SIGPIPE, SIG_IGN);      /* 父进程走了以后写管道别把自己打死（Windows 无此信号） */
#endif

    {
        uint64_t t_open0 = bench_now_us();

        if (from_stdin) {
            if (read_project_blob() != 0) {
                fprintf(stderr, "读工程 blob 失败（F/E 分块格式不对）\n");
                return 3;
            }
            rc = chenge_embed_open_mem(data_dir, err, (int)sizeof(err));
        } else {
            rc = chenge_embed_open(project, data_dir, err, (int)sizeof(err));
        }
        if (bench_n > 0)
            fprintf(stderr, "BENCH open_us=%llu\n",
                    (unsigned long long)(bench_now_us() - t_open0));
    }
    if (rc != 0) {
        fprintf(stderr, "open 失败（%d）：%s\n", rc, err);
        return 3;
    }
    if (bench_n > 0) {
        bench_run(bench_n);
        chenge_embed_close();
        return 0;
    }
    if (chenge_embed_screen(&w, &h) != 0 || w <= 0 || h <= 0) {
        fprintf(stderr, "拿不到屏幕尺寸\n");
        chenge_embed_close();
        return 4;
    }

    map_len = sizeof(frame_hdr_t) + (size_t)w * (size_t)h * 4;
    base = shm_map_create(shm_name, map_len);
    if (!base) {
        chenge_embed_close();
        return 5;
    }

    hdr = (frame_hdr_t*)base;
    hdr->magic = FRAME_MAGIC;
    hdr->version = FRAME_VERSION;
    hdr->w = (uint32_t)w;
    hdr->h = (uint32_t)h;
    hdr->stride = (uint32_t)w * 4;
    hdr->header_bytes = (uint32_t)sizeof(frame_hdr_t);
    hdr->seq = 0;
    hdr->applied_epoch = 0;
    hdr->applied_seq = 0;
    g_hdr = hdr;
    g_base = base;
    g_w = w;
    g_h = h;

    /* 先出第一帧再喊 READY：编辑器收到 READY 就会去读共享内存，
     * 反过来先喊后画会让它读到 seq=0（首帧竞态，实测踩过）。 */
    if (design) {
        /* 设计模式：静止的一张真图，随后只在命令驱动下重绘（不推进时间） */
        chenge_embed_render_once();
    } else {
        chenge_embed_tick();
    }
    publish_frame_force(1);

    fprintf(stderr, "preview-host: 就绪 %dx%d shm=%s design=%d\n", w, h, shm_name, design);
    /* 编辑器不必再轮询共享内存头等魔数：这一行就是"可以开始用"的判据 */
    printf("READY %d %d\n", w, h);
    fflush(stdout);

    for (int frames = 0; !s_quit; frames++) {
        if (!design) {
            chenge_embed_tick();
            /* 这一拍去重：画面没变就不 +seq，客户端不必整屏拷贝 + 重绘 */
            publish_frame_force(0);
        }

        /* 输入：非阻塞地收命令行。EOF = 父进程走了，直接退。 */
        for (;;) {
            struct pollfd pfd;
            char line[64];

            pfd.fd = STDIN_FILENO;
            pfd.events = POLLIN;
            pfd.revents = 0;
            if (poll(&pfd, 1, 0) <= 0 || !(pfd.revents & POLLIN)) break;
            if (!fgets(line, sizeof(line), stdin)) { s_quit = 1; break; }
            if (line[0] == 'Q') { s_quit = 1; break; }
            if (line[0] == 'P') {
                int x = 0, y = 0, down = 0;

                if (sscanf(line + 1, "%d %d %d", &x, &y, &down) == 3)
                    chenge_embed_pointer(x, y, down);
            } else if (line[0] == 'T') {
                /* 变量监视：回一行 `V <名字> <值> <质量>` 到标准输出。
                 * 编辑器按自己的节拍轮询（一页十几个变量，比维护订阅状态简单）。 */
                char name[64] = { 0 };
                int kind;
                double d = 0;
                int b = 0;

                if (sscanf(line + 1, " %63s", name) == 1) {
                    kind = chenge_embed_tag_kind(name);
                    if (kind == 1 && chenge_embed_tag_bool(name, &b))
                        printf("V %s %d %d\n", name, b, chenge_embed_tag_quality(name));
                    else if (kind >= 0 && chenge_embed_tag_num(name, &d))
                        printf("V %s %.6g %d\n", name, d, chenge_embed_tag_quality(name));
                    else
                        printf("V %s ? 0\n", name);
                    fflush(stdout);
                }
            } else if (line[0] == 'S') {
                char* path = line + 1;

                while (*path == ' ') path++;
                path[strcspn(path, "\r\n")] = '\0';
                if (*path && chenge_embed_save_bmp(path) == 0)
                    fprintf(stderr, "preview-host: 截图 %s\n", path);
            } else if (line[0] == 'R') {
                /* 设计模式请求重绘：不推进时间，只把当前模型刷成像素 */
                chenge_embed_render_once();
                publish_frame_force(1);
            } else if (line[0] == 'L' || line[0] == 'G') {
                /* L <epoch> <page>：整页重载（epoch 由客户端递增），page 空 = 当前页重建
                 * G <page>：切页（无 epoch，宿主自己递增 applied_epoch） */
                char page[64] = { 0 };
                unsigned epoch = 0;
                int rc2;

                if (line[0] == 'L') (void)sscanf(line + 1, "%u %63s", &epoch, page);
                else (void)sscanf(line + 1, " %63s", page);

                rc2 = chenge_embed_reload_page(page[0] ? page : NULL);
                chenge_embed_render_once();
                if (rc2 == 0) {
                    g_hdr->applied_epoch = (line[0] == 'L') ? epoch : g_hdr->applied_epoch + 1;
                    g_hdr->applied_seq = 0;
                }
                publish_frame_force(1);
                if (rc2 == 0) printf("A %u 0\n", g_hdr->applied_epoch);
                else printf("E %u 0 patch_rejected reload_failed\n", epoch);
                fflush(stdout);
            } else if (line[0] == 'W') {
                /* W <epoch> <seq> <id> <x> <y> <w> <h>：几何补丁（拖动/缩放）。
                 * 只改位置尺寸、不重建控件 —— 重建会丢事件与绑定（见 ui_page.h）。 */
                unsigned ep = 0, sq = 0;
                char wid[40] = { 0 };
                int x = 0, y = 0, ww = 0, hh = 0;

                if (sscanf(line + 1, "%u %u %39s %d %d %d %d",
                           &ep, &sq, wid, &x, &y, &ww, &hh) == 7) {
                    if (ep != g_hdr->applied_epoch) {
                        /* 页面代不符：这条补丁是对旧页面的，丢 */
                        printf("E %u %u stale_epoch\n", ep, sq);
                    } else if (chenge_embed_patch_rect(wid, x, y, ww, hh) == 0) {
                        chenge_embed_render_once();
                        g_hdr->applied_seq = sq;
                        publish_frame_force(1);
                        printf("A %u %u\n", ep, sq);
                    } else {
                        printf("E %u %u patch_rejected\n", ep, sq);
                    }
                    fflush(stdout);
                }
            } else if (line[0] == 'J') {
                /* J <epoch> <len>\n<len 字节页面 JSON>\n
                 * 用客户端现场生成的快照重建当前页 —— 走 L 会从宿主里的旧工程重读，
                 * 把设计画布刚拖走的控件拉回原位（实测踩过）。 */
                unsigned ep = 0;
                int jlen = 0;

                if (sscanf(line + 1, "%u %d", &ep, &jlen) == 2 && jlen > 0 && jlen < 4 * 1024 * 1024) {
                    char* buf = malloc((size_t)jlen);
                    int rc2 = -1;

                    if (buf) {
                        if (fread(buf, 1, (size_t)jlen, stdin) == (size_t)jlen) {
                            (void)fgetc(stdin);              /* 数据后的换行 */
                            rc2 = chenge_embed_reload_page_json(buf, jlen);
                        }
                        free(buf);
                    }
                    chenge_embed_render_once();
                    if (rc2 == 0) {
                        g_hdr->applied_epoch = ep;
                        g_hdr->applied_seq = 0;
                    }
                    publish_frame_force(1);
                    fprintf(stderr, "preview-host: J epoch=%u len=%d rc=%d\n", ep, jlen, rc2);
                    if (rc2 == 0) printf("A %u 0\n", ep);
                    else printf("E %u 0 reload_failed\n", ep);
                    fflush(stdout);
                }
            } else if (line[0] == 'M') {
                /* 多选成组还没做：明确拒绝，客户端据此退整页重载 */
                printf("E 0 0 patch_rejected not_implemented\n");
                fflush(stdout);
            }
        }

        if (shot && frames == 20) {
            if (chenge_embed_save_bmp(shot) == 0)
                fprintf(stderr, "preview-host: 截图 %s\n", shot);
            shot = NULL;
        }
        if (max_frames > 0 && frames + 1 >= max_frames) break;
        /* 设计模式没有渲染节拍，只等命令；睡太长会让 R 的响应变钝 */
        usleep(design ? 2000 : 16000);
    }

    chenge_embed_close();
    shm_map_destroy(base, map_len);
    fprintf(stderr, "preview-host: 退出\n");
    return 0;
}
