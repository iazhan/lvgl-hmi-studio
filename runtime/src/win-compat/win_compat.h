#ifndef HMI_WIN_COMPAT_H
#define HMI_WIN_COMPAT_H
/*
 * win_compat.h —— Windows（mingw-w64）宿主预览版的 POSIX 补丁层。
 *
 * 用 `-include win-compat/win_compat.h` 强制包含进每个编译单元：
 * 这里只补「Linux 有、Windows 没有，但源码里已经在用」的那几个名字，
 * 于是同一份 .c 不需要到处 #ifdef（那些分支只会让两侧行为各自漂移）。
 *
 * 原则：
 *   · 只补语义等价的（usleep/sleep/mkdir/setenv/clock_gettime）；
 *   · 真正做不了或做不到位的（共享内存、终端 ioctl）留在各自的实现文件里 #ifdef，
 *     因为它们不是"名字缺失"，而是**平台机制不同**；
 *   · 本文件只给 HMI_WINDOWS 用，Linux/板端一行都不经过。
 */
#ifdef _WIN32

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <direct.h>
#include <fcntl.h>
#include <io.h>
#include <process.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <time.h>

/* usleep/sleep：mingw-w64 的 <unistd.h> 直接提供（实测 gcc-13-posix），
 * 不要在这里再 #define 一遍 —— 宏会把系统头里的声明改写成语法错误。 */

/* 两参数的 mkdir(path, mode)：Windows 只有一参数版本，权限位无意义 */
#define mkdir(path, mode) _mkdir(path)

/* setenv/unsetenv：Windows CRT 只有 _putenv_s，这里补成函数（不是宏）——
 * 宏会在任何**之后**被包含的头文件里改写同名声明。实现见 win_compat.c。 */
int setenv(const char* name, const char* value, int overwrite);
int unsetenv(const char* name);

/* 可重入时间转换：Windows 只有 localtime_s/gmtime_s，且**参数顺序相反**
 * （localtime_s(tm, time) vs POSIX localtime_r(time, tm)）。做成同名包装，
 * 调用方照 POSIX 写法即可，也顺带保证两侧线程安全性一致。 */
struct tm;
struct tm* localtime_r(const time_t* time, struct tm* result);
struct tm* gmtime_r(const time_t* time, struct tm* result);

/* 带偏移的读写与落盘：Windows CRT 没有 pread/pwrite/fsync。
 * 实现走 OVERLAPPED，**不动文件指针**（与 POSIX 语义一致）—— 报警/配方存储靠这个
 * 在同一个文件上按记录号读写，若改成先 seek 再读写，多线程访问就会互相踩指针。 */
#include <sys/types.h>
ssize_t pread(int fd, void* buf, size_t count, off_t offset);
ssize_t pwrite(int fd, const void* buf, size_t count, off_t offset);
int     fsync(int fd);

#define getpid  _getpid
#define strcasecmp  _stricmp
#define strncasecmp _strnicmp

/* 说明：clock_gettime / pthread / unistd.h 在 mingw-w64 的 **posix** 线程模型下都
 * 直接可用（实测 gcc-13-posix），不需要在这里补。选错工具链（win32 模型）时
 * 会以 -lpthread 链接失败的形式暴露出来，比运行期行为不同好查。 */

/* 把标准流切到二进制模式：编辑器推给宿主的命令流里混着二进制（页面 JSON 的
 * F/J 分块），文本模式会把 \r\n 翻译掉、把 0x1A 当 EOF —— 表现是"页面加载失败"，
 * 而且只在 Windows 上出现。 */
static inline void hmi_win_set_binary_stdio(void)
{
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stderr), _O_BINARY);
}

#endif /* _WIN32 */
#endif /* HMI_WIN_COMPAT_H */
