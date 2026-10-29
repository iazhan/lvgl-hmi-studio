/*
 * text_conv.h — 设备原始字节 → UTF-8（ADR-0007）
 *
 * Tag DB 里字符串变量存的是设备原始字节，渲染期才按控件声明的 encoding 转码。
 * 这里只做纯数据转换，不依赖 LVGL，宿主与板端共用。
 */
#ifndef HMI_TEXT_CONV_H
#define HMI_TEXT_CONV_H

/* 把 in（n 字节）按 encoding 转成 UTF-8 写进 out（含结尾 0）。
 * enc_gbk = 1 走 GBK 表，0 当 UTF-8 直通（非法序列补 '?'）。
 * 返回写入的字节数（不含结尾 0）。 */
int  text_to_utf8(const char* in, int n, int enc_gbk, char* out, int cap);

/* 按「字符」截断 UTF-8 串（不切半个汉字）；max_chars < 1 视为清空 */
void text_limit_chars(char* s, int max_chars);

#endif /* HMI_TEXT_CONV_H */
