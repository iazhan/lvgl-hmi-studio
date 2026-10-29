/*
 * board_stub_win.c —— 板端专有能力的 Windows 空实现。
 *
 * 被替掉的两块（它们都直接对着设备：U 盘挂载点、块设备/文件系统的剩余空间）：
 *   chenge-common/src/hmi_usb.c             sys/mount.h
 *   chenge-common/src/hmi_project_import.c  sys/statvfs.h
 *
 * 预览宿主不需要它们：编辑器里点"导入/导出配方"在 PC 上本来就没有 U 盘可挂。
 * 一律返回"没有设备/失败"，界面走它原有的失败提示 —— 比"编译不过"或"假装成功"
 * 都诚实。真机行为只在板端/ Linux 宿主上验。
 */
#ifdef HMI_WINDOWS

#include "hmi_project_import.h"
#include "hmi_usb.h"

#include <stddef.h>
#include <stdio.h>

/* ── U 盘 ── */

int hmi_usb_start(hmi_usb_job_t job)
{
    (void)job;
    return -1;
}

hmi_usb_state_t hmi_usb_step(void)
{
    return HMI_USB_IDLE;
}

const char* hmi_usb_message(void)
{
    return "PC 预览不支持 U 盘操作";
}

void hmi_usb_reset(void)
{
}

int hmi_usb_mount(int rw)
{
    (void)rw;
    return -1;
}

void hmi_usb_umount(void)
{
}

/* ── 工程导入 ── */

int hmi_import_find(const char* usb_root, char* dir_out, size_t outsz)
{
    (void)usb_root;
    if (dir_out && outsz) dir_out[0] = '\0';
    return -1;
}

int hmi_import_check(const char* project_dir, char* id_out, size_t idsz,
                     char* err, size_t errsz)
{
    (void)project_dir;
    if (id_out && idsz) id_out[0] = '\0';
    if (err && errsz) snprintf(err, errsz, "PC 预览不支持导入工程");
    return -1;
}

long long hmi_import_tree_size(const char* dir)
{
    (void)dir;
    return -1;
}

int hmi_import_copy_tree(const char* src, const char* dst, char* err, size_t errsz)
{
    (void)src; (void)dst;
    if (err && errsz) snprintf(err, errsz, "PC 预览不支持导入工程");
    return -1;
}

int hmi_import_activate(const char* tmp_dir, const char* id, char* err, size_t errsz)
{
    (void)tmp_dir; (void)id;
    if (err && errsz) snprintf(err, errsz, "PC 预览不支持导入工程");
    return -1;
}

const char* hmi_import_active_id(void)
{
    return "";
}

int hmi_import_rm_tree(const char* dir)
{
    (void)dir;
    return -1;
}

#endif /* HMI_WINDOWS */
