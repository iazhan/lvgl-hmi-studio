/*
 * ui_image_bin.c — 组态图片 .bin 的加载与校验（37 §6.15）
 */
#include "ui_image_bin.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define IMG_MAX_SIDE   2048
#define IMG_MAX_BYTES  (2 * 1024 * 1024)

static uint16_t rd16(const uint8_t* p)
{
    return (uint16_t)(p[0] | (p[1] << 8));
}

static uint32_t rd32(const uint8_t* p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint32_t crc32_calc(const uint8_t* p, size_t n)
{
    uint32_t c = 0xFFFFFFFFu;

    for (size_t i = 0; i < n; i++) {
        c ^= p[i];
        for (int b = 0; b < 8; b++)
            c = (c & 1u) ? ((c >> 1) ^ 0xEDB88320u) : (c >> 1);
    }
    return c ^ 0xFFFFFFFFu;
}

int image_bin_path(const char* root, const char* src, char* out, size_t outsz)
{
    int n;

    if (!root || !src || !*src) return -1;
    /* 工程内相对路径：拒绝绝对路径与 ..，防止工程指向工程外 */
    if (src[0] == '/' || strstr(src, "..")) return -1;
    n = snprintf(out, outsz, "%s/assets/img/%s", root, src);
    return (n < 0 || (size_t)n >= outsz) ? -1 : 0;
}

int image_bin_load(const char* path, lv_image_dsc_t* dsc, void** pixels,
                   char* err, size_t errsz)
{
    uint8_t hdr[32];
    uint8_t* buf;
    FILE* f;
    int w, h, fmt;
    uint32_t stride, size, crc, expect;

    f = fopen(path, "rb");
    if (!f) {
        snprintf(err, errsz, "打不开 %s", path);
        return -1;
    }
    if (fread(hdr, 1, sizeof(hdr), f) != sizeof(hdr)) {
        fclose(f);
        snprintf(err, errsz, "%s: 头部不足 32 字节", path);
        return -1;
    }
    if (memcmp(hdr, "IMG1", 4) != 0) {
        fclose(f);
        snprintf(err, errsz, "%s: magic 不是 IMG1", path);
        return -1;
    }
    if (rd16(hdr + 4) != 32 || rd16(hdr + 6) != 1) {
        fclose(f);
        snprintf(err, errsz, "%s: header_size/version 不支持（%u/%u）",
                 path, rd16(hdr + 4), rd16(hdr + 6));
        return -1;
    }
    w = rd16(hdr + 8);
    h = rd16(hdr + 10);
    fmt = hdr[12];
    if (w < 1 || w > IMG_MAX_SIDE || h < 1 || h > IMG_MAX_SIDE) {
        fclose(f);
        snprintf(err, errsz, "%s: 尺寸 %dx%d 超出 1..%d", path, w, h, IMG_MAX_SIDE);
        return -1;
    }
    if (fmt != 0 && fmt != 1) {
        fclose(f);
        snprintf(err, errsz, "%s: format %d 未知（0=RGB565, 1=RGB565A8）", path, fmt);
        return -1;
    }
    if (hdr[13] != 0) {
        fclose(f);
        snprintf(err, errsz, "%s: flags 必须为 0", path);
        return -1;
    }
    stride = rd32(hdr + 16);
    size = rd32(hdr + 20);
    crc = rd32(hdr + 24);

    if (stride < (uint32_t)w * 2) {
        fclose(f);
        snprintf(err, errsz, "%s: stride %u 小于一行像素 %d", path, stride, w * 2);
        return -1;
    }
    expect = (uint32_t)h * stride + (fmt == 1 ? (uint32_t)h * (uint32_t)w : 0);
    if (size != expect) {
        fclose(f);
        snprintf(err, errsz, "%s: data_size %u 与尺寸算出的 %u 不一致",
                 path, size, expect);
        return -1;
    }
    if (size > IMG_MAX_BYTES) {
        fclose(f);
        snprintf(err, errsz, "%s: 像素数据 %.1f MB 超过 2MB",
                 path, size / 1048576.0);
        return -1;
    }

    buf = malloc(size);
    if (!buf) {
        fclose(f);
        snprintf(err, errsz, "%s: 内存不足（需要 %u 字节）", path, size);
        return -1;
    }
    if (fread(buf, 1, size, f) != size) {
        free(buf);
        fclose(f);
        snprintf(err, errsz, "%s: 像素数据不完整", path);
        return -1;
    }
    fclose(f);

    if (crc32_calc(buf, size) != crc) {
        free(buf);
        snprintf(err, errsz, "%s: CRC32 校验失败", path);
        return -1;
    }

    memset(dsc, 0, sizeof(*dsc));
    dsc->header.magic = LV_IMAGE_HEADER_MAGIC;
    dsc->header.cf = (fmt == 1) ? LV_COLOR_FORMAT_RGB565A8 : LV_COLOR_FORMAT_RGB565;
    dsc->header.w = (uint32_t)w;
    dsc->header.h = (uint32_t)h;
    dsc->header.stride = stride;
    dsc->data_size = size;
    dsc->data = buf;
    *pixels = buf;
    return 0;
}
