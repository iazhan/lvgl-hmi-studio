/*
 * pkg_selftest.c — 宿主判据：.cgpkg 解包（37 §13）
 *
 * 板端解包代码（chenge-common/src/hmi_project_import.c）本身与界面无关，
 * 可以在宿主编出来直接跑：包由 tools/make_cgpkg.py 独立生成（第二套实现按文档写），
 * 它读得出来才说明两边没跑偏。
 *
 * 用法：pkg_selftest <期望工程目录> <好包> [<坏包> ...]
 *   好包：解包到临时目录后与期望目录**逐文件比对**（集合 + 内容），再跑一遍
 *         hmi_import_check 拿到 <id>
 *   坏包：必须失败，且失败后目标目录不能留下半截（包坏了却留下一个能加载的工程，
 *         比直接报错危险得多）
 */
#include "hmi_project_import.h"

#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

static int g_fail;
static int g_case;

static void ok(int cond, const char* what)
{
    if (!cond) {
        printf("  FAIL [%d] %s\n", g_case, what);
        g_fail++;
    }
}

/* ── 逐文件比对：目录树里每个普通文件的内容必须一致，且两边文件集合相同 ── */

static int cmp_file(const char* a, const char* b)
{
    FILE* fa = fopen(a, "rb");
    FILE* fb = fopen(b, "rb");
    unsigned char ba[4096], bb[4096];
    int same = 1;

    if (!fa || !fb) {
        if (fa) fclose(fa);
        if (fb) fclose(fb);
        return 0;
    }
    for (;;) {
        size_t na = fread(ba, 1, sizeof(ba), fa);
        size_t nb = fread(bb, 1, sizeof(bb), fb);
        if (na != nb || memcmp(ba, bb, na) != 0) { same = 0; break; }
        if (na == 0) break;
    }
    fclose(fa);
    fclose(fb);
    return same;
}

static int count_files(const char* dir);

static int cmp_tree(const char* want, const char* got)
{
    DIR* d = opendir(want);
    struct dirent* de;
    int okall = 1;

    if (!d) return 0;
    while ((de = readdir(d)) != NULL) {
        char pa[600], pb[600];
        struct stat st;

        if (strcmp(de->d_name, ".") == 0 || strcmp(de->d_name, "..") == 0) continue;
        snprintf(pa, sizeof(pa), "%s/%s", want, de->d_name);
        snprintf(pb, sizeof(pb), "%s/%s", got, de->d_name);
        if (lstat(pa, &st) != 0) { okall = 0; continue; }
        if (S_ISDIR(st.st_mode)) {
            if (!cmp_tree(pa, pb)) okall = 0;
        } else if (S_ISREG(st.st_mode)) {
            if (!cmp_file(pa, pb)) {
                printf("    内容不一致：%s\n", pa);
                okall = 0;
            }
        }
    }
    closedir(d);
    return okall;
}

static int count_files(const char* dir)
{
    DIR* d = opendir(dir);
    struct dirent* de;
    int n = 0;

    if (!d) return -1;
    while ((de = readdir(d)) != NULL) {
        char p[600];
        struct stat st;

        if (strcmp(de->d_name, ".") == 0 || strcmp(de->d_name, "..") == 0) continue;
        snprintf(p, sizeof(p), "%s/%s", dir, de->d_name);
        if (lstat(p, &st) != 0) continue;
        if (S_ISDIR(st.st_mode)) n += count_files(p);
        else n++;
    }
    closedir(d);
    return n;
}

int main(int argc, char** argv)
{
    const char* want;
    const char* good;
    char out[600];
    char err[HMI_IMPORT_ERR_MAX] = "";
    char id[HMI_IMPORT_ID_MAX] = "";
    long long payload;
    int i;

    if (argc < 3) {
        printf("用法：pkg_selftest <期望工程目录> <好包> [<坏包> ...]\n");
        return 2;
    }
    want = argv[1];
    good = argv[2];

    printf("== 好包：%s\n", good);
    g_case = 1;
    ok(hmi_import_is_pkg(good) == 1, "hmi_import_is_pkg 没认出这个包");

    payload = hmi_import_pkg_payload_size(good, err, sizeof(err));
    ok(payload > 0, err[0] ? err : "包头读数失败");
    if (payload > 0)
        printf("  载荷 %lld 字节\n", payload);

    snprintf(out, sizeof(out), "/tmp/pkg_selftest_unpack");
    hmi_import_rm_tree(out);
    ok(hmi_import_unpack_pkg(good, out, err, sizeof(err)) == 0, err);
    ok(count_files(out) == count_files(want), "解包后的文件数与期望目录不同");
    ok(cmp_tree(want, out), "解包内容与期望目录不一致");

    err[0] = '\0';
    ok(hmi_import_check(out, id, sizeof(id), err, sizeof(err)) == 0, err[0] ? err : "manifest 校验失败");
    {
        /* 期望 id：默认自测夹具的 pkgtest；留样做跨仓库互通时（编辑器导出的包）
         * 用 PKG_EXPECT_ID 指定 —— 工程 id 必须是小写字母/数字/下划线，中文名会被拒装。 */
        const char* want_id = getenv("PKG_EXPECT_ID");

        ok(strcmp(id, want_id && *want_id ? want_id : "pkgtest") == 0,
           "工程 id 不是 manifest 里的 name");
    }
    hmi_import_rm_tree(out);

    for (i = 3; i < argc; i++) {
        printf("== 坏包：%s\n", argv[i]);
        g_case++;
        err[0] = '\0';
        snprintf(out, sizeof(out), "/tmp/pkg_selftest_bad");
        hmi_import_rm_tree(out);
        ok(hmi_import_unpack_pkg(argv[i], out, err, sizeof(err)) != 0, "坏包居然解包成功");
        ok(err[0] != '\0', "失败但没给原因（界面只能显示空白）");
        if (err[0]) printf("  拒绝原因：%s\n", err);
        {
            struct stat st;

            ok(stat(out, &st) != 0, "失败后留下了半截目标目录");
        }
    }

    printf(g_fail ? "失败 %d 项\n" : "全部通过（%d 组）\n", g_fail ? g_fail : g_case);
    return g_fail ? 1 : 0;
}
