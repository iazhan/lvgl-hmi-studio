/*
 * sim_off.c — WITH_SIM=0 时的空实现
 *
 * 产品镜像不需要"假设备"：sim.c 的寄存器模型与 sim_cfg.c 的 sim.json / sim.lua 都不编，
 * 这里只补链接所需的符号。语义等价于"工程里没有 sim 设备"——装载期 loader 会因
 * caps 没有 sim 模块而拒绝 protocol=sim 的设备，所以运行期不会走到这些函数。
 */
#include "comm/sim.h"
#include "comm/sim_cfg.h"

void sim_init(sim_dev_t* s)
{
    (void)s;
}

void sim_tick(sim_dev_t* s, uint32_t elapsed_ms)
{
    (void)s;
    (void)elapsed_ms;
}

int sim_read(const sim_dev_t* s, int area, int addr, int datatype,
             double* num, int* boolean)
{
    (void)s; (void)area; (void)addr; (void)datatype;
    (void)num; (void)boolean;
    return -1;
}

int sim_write(sim_dev_t* s, int area, int addr, int datatype,
              double num, int boolean)
{
    (void)s; (void)area; (void)addr; (void)datatype; (void)num; (void)boolean;
    return -1;
}

void sim_sync_tags(const sim_dev_t* s, const tag_def_t* tags, int count)
{
    (void)s;
    (void)tags;
    (void)count;
}

int sim_write_tag(sim_dev_t* s, const tag_def_t* t, double num, int boolean)
{
    (void)s; (void)t; (void)num; (void)boolean;
    return -1;
}

int sim_write_str(sim_dev_t* s, const tag_def_t* t, const char* bytes, int n)
{
    (void)s; (void)t; (void)bytes; (void)n;
    return -1;
}

int sim_cfg_load(const project_t* p, sim_dev_t* sim, char* err, size_t errsz)
{
    (void)p;
    (void)sim;
    if (err && errsz) err[0] = '\0';
    return 0;
}

void sim_cfg_tick(sim_dev_t* sim, uint32_t now_ms)
{
    (void)sim;
    (void)now_ms;
}

void sim_cfg_on_write(int tag, double value)
{
    (void)tag;
    (void)value;
}

int sim_cfg_active(void)
{
    return 0;
}

const char* sim_cfg_take_error(void)
{
    return NULL;
}
