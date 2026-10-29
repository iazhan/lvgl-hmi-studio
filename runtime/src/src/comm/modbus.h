/*
 * modbus.h — Modbus RTU 主站（每串口一个通讯线程）
 *
 * 只实现 v1 需要的部分：读线圈/离散输入/保持寄存器/输入寄存器（FC 01–04），
 * 写单线圈/单寄存器/多寄存器（FC 05/06/16）。TCP 与 S7 不在范围内。
 *
 * 线程模型（34 §5.4）：通讯线程只读写 Tag DB 与自己的写队列，绝不碰 LVGL；
 * 界面侧通过 modbus_write_tag() 把写请求投进队列，由线程串行下发。
 */
#ifndef HMI_MODBUS_H
#define HMI_MODBUS_H

#include "project/loader.h"

/* 为每个用到 modbus_rtu 的串口起一个通讯线程。返回线程数；<0 表示一个都没起来 */
int  modbus_start(const project_t* p);

/* 把一次写请求投进变量所在设备的队列（tagdb 写成功之后调用）。
 * 变量没有 modbus source 时是空操作返回 0；队列满返回 -1。 */
int  modbus_write_tag(const project_t* p, int tag_id, double num, int boolean);

/* 字符串写：按 fc16 把 n 个字节铺进连续的 holding 寄存器（2 字节/寄存器，末尾补 0） */
int  modbus_write_tag_str(const project_t* p, int tag_id, const char* s, int n);

/* 设备链路是否在线：1 在线、0 断线、-1 查不到（sim 设备恒为 1）。
 * 通讯报警（alarms.json 的 type=comm）按它判定。 */
int  modbus_device_online(const project_t* p, const char* dev_id);

/* 启停某个设备的轮询（37 §7.2 的 cg.startDevice/stopDevice）。
 * 停用只影响读轮询，写队列照常下发。设备不存在返回 -1。 */
int  modbus_device_enable(const project_t* p, const char* dev_id, int on);

/* 直接写设备寄存器/线圈（37 §6.7 的 write_device）：不经过变量表。
 * 只接受 modbus_rtu 设备；队列满返回 -1。 */
int  modbus_write_device(const project_t* p, const char* dev_id, int area, int addr,
                         int datatype, double num, int boolean);

/* 通知所有线程退出并等它们收尾（正常运行时不需要调用） */
void modbus_stop(void);

#endif /* HMI_MODBUS_H */
