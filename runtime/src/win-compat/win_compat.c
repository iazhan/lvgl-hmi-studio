/*
 * win_compat.c —— Windows 预览宿主缺的几样 POSIX 机制（见 win_compat.h / poll.h）。
 *
 * 目前只有一件：poll()。用 PeekNamedPipe 判断管道里有没有数据 —— 预览宿主的 stdin
 * 正是编辑器开的管道，而原来的调用形态就是「非阻塞问一下有没有命令」。
 * 若 fd 不是管道（控制台/文件），一律当"没有数据"，让主循环照常推进时间。
 */
#ifdef _WIN32

#include "poll.h"
#include "win_compat.h"

/* setenv/unsetenv：CRT 的 _putenv_s 语义与 POSIX 一致（overwrite=0 时保留旧值） */
int setenv(const char* name, const char* value, int overwrite)
{
    if (!name || !*name) return -1;
    if (!overwrite && getenv(name)) return 0;
    return _putenv_s(name, value ? value : "") == 0 ? 0 : -1;
}

int unsetenv(const char* name)
{
    if (!name || !*name) return -1;
    return _putenv_s(name, "") == 0 ? 0 : -1;
}

ssize_t pread(int fd, void* buf, size_t count, off_t offset)
{
    HANDLE h = (HANDLE)_get_osfhandle(fd);
    OVERLAPPED ov;
    DWORD got = 0;

    if (h == INVALID_HANDLE_VALUE || count == 0) return -1;
    memset(&ov, 0, sizeof(ov));
    ov.Offset = (DWORD)((unsigned long long)offset & 0xFFFFFFFFull);
    ov.OffsetHigh = (DWORD)(((unsigned long long)offset >> 32) & 0xFFFFFFFFull);
    if (!ReadFile(h, buf, (DWORD)count, &got, &ov)) return -1;
    return (ssize_t)got;
}

ssize_t pwrite(int fd, const void* buf, size_t count, off_t offset)
{
    HANDLE h = (HANDLE)_get_osfhandle(fd);
    OVERLAPPED ov;
    DWORD put = 0;

    if (h == INVALID_HANDLE_VALUE || count == 0) return -1;
    memset(&ov, 0, sizeof(ov));
    ov.Offset = (DWORD)((unsigned long long)offset & 0xFFFFFFFFull);
    ov.OffsetHigh = (DWORD)(((unsigned long long)offset >> 32) & 0xFFFFFFFFull);
    if (!WriteFile(h, buf, (DWORD)count, &put, &ov)) return -1;
    return (ssize_t)put;
}

int fsync(int fd)
{
    return _commit(fd) == 0 ? 0 : -1;
}

struct tm* localtime_r(const time_t* time, struct tm* result)
{
    if (!time || !result) return NULL;
    return localtime_s(result, time) == 0 ? result : NULL;
}

struct tm* gmtime_r(const time_t* time, struct tm* result)
{
    if (!time || !result) return NULL;
    return gmtime_s(result, time) == 0 ? result : NULL;
}

int poll(struct pollfd* fds, unsigned long nfds, int timeout_ms)
{
    unsigned long i;
    int ready = 0;

    if (!fds || nfds == 0) {
        if (timeout_ms > 0) Sleep((DWORD)timeout_ms);
        return 0;
    }

    for (i = 0; i < nfds; i++) {
        HANDLE h;
        DWORD avail = 0;

        fds[i].revents = 0;
        if (fds[i].fd < 0) continue;

        h = (HANDLE)_get_osfhandle(fds[i].fd);
        if (h == INVALID_HANDLE_VALUE) continue;

        if (PeekNamedPipe(h, NULL, 0, NULL, &avail, NULL) && avail > 0) {
            fds[i].revents = (short)(fds[i].events ? fds[i].events : POLLIN);
            ready++;
        }
    }

    if (ready == 0 && timeout_ms > 0) Sleep((DWORD)timeout_ms);
    return ready;
}

#endif /* _WIN32 */
