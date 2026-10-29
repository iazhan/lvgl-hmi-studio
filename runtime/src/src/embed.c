/*
 * embed.c —— 嵌入宿主用的薄封装：把 runtime_boot / runtime_tick / 变量表
 * 包成一组稳定 C ABI（见 include/chenge_embed.h）。
 *
 * 这里**不放**任何显示/输入实现：那是平台层的事（hmi_ui_embed.c）。
 * 好处是同一份业务代码既能在 .so 里跑，也能被 chenge-preview-host（独立进程）
 * 链接 —— 「进程内预览」与「隔离预览」用的不是两套实现。
 */
#include "chenge_embed.h"

#include "hmi_log.h"

#include "core/tagdb.h"
#include "core/vfs.h"
#include "project/loader.h"
#include "runtime_boot.h"
#include "ui/ui_page.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

static project_t s_proj;
static int       s_open;
static char      s_err[256];

/* 显示资源归平台层（hmi_ui_embed.c），close 时连帧缓冲一起作废 */
void hmi_embed_display_down(void);

int chenge_embed_abi(void)
{
    return CHENGE_EMBED_ABI;
}

const char* chenge_embed_last_error(void)
{
    return s_err;
}

static int do_open(const char* project_dir, const char* data_dir, char* err, int errlen)
{
    int rc;

    if (s_open) {
        snprintf(s_err, sizeof(s_err), "已经打开了一个工程（先 close）");
        if (err && errlen > 0) snprintf(err, (size_t)errlen, "%s", s_err);
        return 1;
    }

    /* 运行时的若干子系统读环境变量（串口映射、数据目录、变量追踪）。
     * 库被同一进程反复加载时 setenv 会累积，所以每次 open 都显式覆盖一遍。 */
    setenv("HMI_PROJECT_DIR", project_dir, 1);
    if (data_dir && *data_dir) setenv("HMI_DATA_DIR", data_dir, 1);
    /* 内存表非空时，装载器的每个读文件都会先查它（相对路径基准 = 工程根） */
    vfs_set_root(project_dir);

    memset(&s_proj, 0, sizeof(s_proj));
    rc = runtime_boot(&s_proj, project_dir, s_err, sizeof(s_err));
    if (rc != RT_BOOT_OK) {
        if (err && errlen > 0) snprintf(err, (size_t)errlen, "%s", s_err);
        return rc;
    }

    s_open = 1;
    return 0;
}

int chenge_embed_open(const char* project_dir, const char* data_dir, char* err, int errlen)
{
    if (!project_dir || !*project_dir) {
        snprintf(s_err, sizeof(s_err), "没有给工程目录");
        if (err && errlen > 0) snprintf(err, (size_t)errlen, "%s", s_err);
        return 1;
    }
    return do_open(project_dir, data_dir, err, errlen);
}

/* ── 内存工程（预览不落盘） ── */

void chenge_embed_project_clear(void)
{
    vfs_clear();
}

int chenge_embed_project_put(const char* path, const void* data, int len)
{
    if (!path || !*path || !data || len <= 0) return -1;
    return vfs_put(path, data, (size_t)len);
}

int chenge_embed_open_mem(const char* data_dir, char* err, int errlen)
{
    char root[300];
    const char* base = (data_dir && *data_dir) ? data_dir : ".";

    if (vfs_count() == 0) {
        snprintf(s_err, sizeof(s_err), "内存里没有工程文件（先 chenge_embed_project_put）");
        if (err && errlen > 0) snprintf(err, (size_t)errlen, "%s", s_err);
        return 1;
    }

    /* 工程根的占位目录：装载器对内存表里的文件不 stat，但趋势/审计要一个
     * 可写基准，且错误信息里带 root 更可读。空目录，不写任何工程文件。 */
    snprintf(root, sizeof(root), "%s/project", base);
    (void)mkdir(root, 0755);

    return do_open(root, base, err, errlen);
}

void chenge_embed_tick(void)
{
    if (s_open) runtime_tick();
}

void chenge_embed_render_once(void)
{
    if (s_open) runtime_render_once();
}

int chenge_embed_reload_page(const char* page_id)
{
    if (!s_open) return -1;

    /* 空 = 重建当前页（改绑定/权限这类慢路径）；非空 = 切页。
     * 两条都复用运行时自己的入口，不另写一条"预览专用"的重载逻辑。 */
    if (!page_id || !*page_id) return page_mgr_reload();
    return page_mgr_script_open(page_id);
}

int chenge_embed_patch_rect(const char* widget_id, int x, int y, int w, int h)
{
    char err[160];

    if (!s_open) return -1;
    return ui_page_patch_rect(widget_id, x, y, w, h, err, sizeof(err));
}

int chenge_embed_reload_page_json(const char* json, int len)
{
    char err[192];

    if (!s_open) return -1;
    return page_mgr_reload_json(json, (size_t)len, err, sizeof(err));
}

void chenge_embed_close(void)
{
    if (!s_open) return;

    runtime_shutdown();
    project_free(&s_proj);
    hmi_embed_display_down();
    s_open = 0;
}

/* ── 变量读取（按名字；编辑器断言"点了按钮以后变量变了"用它） ── */

static int tag_id_of(const char* name)
{
    if (!s_open || !name || !*name) return -1;

    for (int i = 0; i < s_proj.tags_count; i++) {
        if (s_proj.tags[i].name && strcmp(s_proj.tags[i].name, name) == 0) return i;
    }
    return -1;
}

int chenge_embed_tag_num(const char* name, double* out)
{
    int id = tag_id_of(name);

    if (id < 0 || !out) return 0;
    *out = tagdb_num(id);
    return 1;
}

int chenge_embed_tag_bool(const char* name, int* out)
{
    int id = tag_id_of(name);

    if (id < 0 || !out) return 0;
    *out = tagdb_bool(id);
    return 1;
}

int chenge_embed_tag_quality(const char* name)
{
    int id = tag_id_of(name);

    return id < 0 ? -1 : tagdb_quality(id);
}

int chenge_embed_tag_kind(const char* name)
{
    int id = tag_id_of(name);
    const tag_t* t;

    if (id < 0) return -1;
    t = tagdb_at(id);
    if (!t) return -1;
    if (t->type == TAG_T_BOOL) return 1;
    if (t->type == TAG_T_STRING) return 2;
    return 0;
}
