/*
 * ui_image_bin.h — 组态图片 .bin 的加载与校验（37 §6.15）
 *
 * 32 字节头 + RGB565 / RGB565A8 像素数据。运行时逐项校验 magic/version/尺寸/
 * stride/data_size/CRC：坏图必须报错并显示占位，不能让 LVGL 拿着错的数据去画。
 */
#ifndef HMI_UI_IMAGE_BIN_H
#define HMI_UI_IMAGE_BIN_H

#include "lvgl.h"

#include <stddef.h>

/* 读一张 .bin 到内存，填好 LVGL 的图片描述符。
 * 成功返回 0，*pixels 是 malloc 出来的像素缓冲（调用方负责 free）；
 * 失败返回 -1 并写 err。 */
int image_bin_load(const char* path, lv_image_dsc_t* dsc, void** pixels,
                   char* err, size_t errsz);

/* 拼资源路径：<root>/assets/img/<src>；src 里出现 .. 或以 / 开头都拒绝 */
int image_bin_path(const char* root, const char* src, char* out, size_t outsz);

#endif /* HMI_UI_IMAGE_BIN_H */
