/*
 * vfs —— 工程文件的第二种来源：内存（预览时工程还没落盘）
 *
 * 板子与宿主的正常路径是"从工程目录读文件"。编辑器预览时工程只是内存里的模型，
 * 序列化出来可以直接交过来，不必先写临时目录 —— 这既省 IO，也避开
 * "Windows 用户目录含中文 / 只读安装目录" 这类与环境相关的失败。
 *
 * 边界：**只覆盖工程本体**（manifest/tags/devices/ports/pages/strings/alarms/
 * savecfg/users/recipes 与 assets）。运行期写入（日志/审计/趋势/存盘组）仍然走
 * HMI_DATA_DIR 的真实目录 —— 预览绝不能往工程里写东西。
 *
 * 表为空时所有查询立即返回未命中，进程版（板端/宿主）零开销、行为不变。
 */
#ifndef CHENGE_CORE_VFS_H
#define CHENGE_CORE_VFS_H

#include <stddef.h>

/* 清空内存表（每次 open 前调一次，避免上一个工程的文件残留） */
void vfs_clear(void);

/* 相对路径的基准（工程根）。设了之后 abs 路径会自动剥前缀再查表。 */
void vfs_set_root(const char* root);

/* 放一个文件。path 是**相对工程根**的路径（"pages/home.json"）。返回 0 成功。 */
int vfs_put(const char* path, const void* data, size_t len);

/*
 * 查一个文件。abs_or_rel 可以是绝对路径（会被 root 剥成相对）或已经是相对路径。
 * 命中返回数据指针（表内持有，调用方不要 free），*len 出门；未命中返回 NULL。
 */
const void* vfs_get(const char* abs_or_rel, size_t* len);

int vfs_count(void);

#endif /* CHENGE_CORE_VFS_H */
