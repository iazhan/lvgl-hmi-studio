/*
 * modbus_win_stub.c —— Windows 预览宿主版的总线空实现。
 *
 * 为什么需要它：`comm/modbus.c` 是 socket + 独立线程的现场总线客户端，预览宿主
 * 不需要它（也不该在 PC 上开一堆 TCP 连接），但 `runtime_boot.c` / 页面动作 /
 * 报警 / 写变量这几处会调它的接口。所以 Windows 版把它整块换掉 —— 只保留**接口
 * 形状**，一律报"没有设备/写不进去"，让预览照常跑页与绑定。
 *
 * 判据：Windows 包里点"写变量/切设备"这类动作应当没有副作用；要验真的总线行为，
 * 只能在板端或 Linux 宿主上做（那条路径走真实现）。
 */
/* 只在 WINDOWS=1 时被编进来（见 Makefile 的 SRCS_EXTRA），所以不需要 #ifdef 兜底。 */
#include "modbus.h"

int modbus_start(const project_t* p)
{
    (void)p;
    return 0;
}

void modbus_stop(void)
{
}

int modbus_write_tag(const project_t* p, int tag_id, double num, int boolean)
{
    (void)p; (void)tag_id; (void)num; (void)boolean;
    return -1;
}

int modbus_write_tag_str(const project_t* p, int tag_id, const char* s, int n)
{
    (void)p; (void)tag_id; (void)s; (void)n;
    return -1;
}

int modbus_device_online(const project_t* p, const char* dev_id)
{
    (void)p; (void)dev_id;
    return 0;
}

int modbus_device_enable(const project_t* p, const char* dev_id, int on)
{
    (void)p; (void)dev_id; (void)on;
    return -1;
}

int modbus_write_device(const project_t* p, const char* dev_id, int area, int addr,
                        int datatype, double num, int boolean)
{
    (void)p; (void)dev_id; (void)area; (void)addr; (void)datatype; (void)num; (void)boolean;
    return -1;
}

