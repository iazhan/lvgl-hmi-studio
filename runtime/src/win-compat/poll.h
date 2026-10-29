#ifndef HMI_WIN_POLL_H
#define HMI_WIN_POLL_H
/*
 * win-compat/poll.h —— mingw-w64 没有 <poll.h>，这里补一份够本仓库用的。
 *
 * 本仓库只有两处真的调 poll()：
 *   · preview_host.c：非阻塞看 stdin 有没有命令（fd 是编辑器开的管道）；
 *   · comm/modbus.c：看 socket 可读（Windows 预览版里这个文件被排除了，见 Makefile）。
 * 所以实现按"管道优先、其余当不可读"来写，见 win_compat.c。这与 Linux 上那一处
 * 用法完全一致，不需要模拟 poll 的全部语义。
 */
#ifdef _WIN32

#include <stddef.h>

#ifndef POLLIN
#define POLLIN  0x0001
#define POLLPRI 0x0002
#define POLLOUT 0x0004
#define POLLERR 0x0008
#define POLLHUP 0x0010
#define POLLNVAL 0x0020
#endif

struct pollfd {
    int   fd;
    short events;
    short revents;
};

#ifdef __cplusplus
extern "C" {
#endif

/* 返回值与 POSIX 一致：>0 就绪个数，0 超时，<0 出错。 */
int poll(struct pollfd* fds, unsigned long nfds, int timeout_ms);

#ifdef __cplusplus
}
#endif

#endif /* _WIN32 */
#endif /* HMI_WIN_POLL_H */
