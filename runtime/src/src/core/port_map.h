/*
 * port_map.h — 逻辑串口名（ports.json 的 id）→ 设备节点
 *
 * 映射在编译期按板定死（ADR 0001）：串口是硬件属性，工程只写 com1 这类逻辑名，
 * 换板时不该改工程。加载期据此校验 ports.json 里的 id 本板是否真的有。
 */
#ifndef HMI_PORT_MAP_H
#define HMI_PORT_MAP_H

/* 本板声明的逻辑串口（查表，不受宿主覆盖影响）。有返回 1 */
int port_known(const char* id);

/* 设备节点；未知 id 返回 NULL。宿主构建可用 HMI_PORT_<ID> 覆盖（如
 * HMI_PORT_COM1=/dev/pts/7）——板端不认这个变量，避免现场被误设。 */
const char* port_node(const char* id);

#endif /* HMI_PORT_MAP_H */
