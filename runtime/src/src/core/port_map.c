/*
 * port_map.c — 板级串口映射
 *
 * kinseal 只有 UART2（PE7/PE8）能拿来做业务串口；UART0 是调试控制台。
 * dts 没有 aliases，节点号按驱动注册顺序给：实测 /proc/tty/driver/serial 里
 * 0x01C25800（UART2）对应 /dev/ttyS1，0x01C25000（UART0）是 ttyS0。
 */
#include "port_map.h"
#include "hmi_board.h"

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const struct {
    const char* id;
    const char* node;
} s_map[] = {
    { "com1", "/dev/ttyS1" },
};

int port_known(const char* id)
{
    if (!id || !*id) return 0;
    for (size_t i = 0; i < sizeof(s_map) / sizeof(s_map[0]); i++)
        if (strcmp(s_map[i].id, id) == 0) return 1;
    return 0;
}

const char* port_node(const char* id)
{
    if (!id || !*id) return NULL;

#ifdef HMI_HOST_BUILD
    {
        char env[32];
        const char* p = id;
        size_t n = strlen("HMI_PORT_");
        char* w;

        snprintf(env, sizeof(env), "HMI_PORT_");
        w = env + n;
        while (*p && w < env + sizeof(env) - 1)
            *w++ = (char)toupper((unsigned char)*p++);
        *w = '\0';
        {
            const char* o = getenv(env);
            if (o && *o) return o;
        }
    }
#endif

    for (size_t i = 0; i < sizeof(s_map) / sizeof(s_map[0]); i++)
        if (strcmp(s_map[i].id, id) == 0) return s_map[i].node;
    return NULL;
}
