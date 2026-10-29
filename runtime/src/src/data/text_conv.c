/*
 * text_conv.c — 设备原始字节 → UTF-8（ADR-0007）
 *
 * 两条路径：GBK 查表（src/data/gbk_table.c，生成物）与 UTF-8 直通（校验非法序列）。
 * 转换结果一律以 0 结尾，调用方用 text_limit_chars() 按字符数截断。
 */
#include "text_conv.h"
#include "gbk_table.h"

/* 把一个 Unicode 码位写成 UTF-8，返回字节数 */
static int utf8_put(uint32_t cp, char* out, int cap)
{
    if (cp < 0x80) {
        if (cap < 2) return 0;
        out[0] = (char)cp;
        return 1;
    }
    if (cp < 0x800) {
        if (cap < 3) return 0;
        out[0] = (char)(0xC0 | (cp >> 6));
        out[1] = (char)(0x80 | (cp & 0x3F));
        return 2;
    }
    if (cp < 0x10000) {
        if (cap < 4) return 0;
        out[0] = (char)(0xE0 | (cp >> 12));
        out[1] = (char)(0x80 | ((cp >> 6) & 0x3F));
        out[2] = (char)(0x80 | (cp & 0x3F));
        return 3;
    }
    if (cap < 5) return 0;
    out[0] = (char)(0xF0 | (cp >> 18));
    out[1] = (char)(0x80 | ((cp >> 12) & 0x3F));
    out[2] = (char)(0x80 | ((cp >> 6) & 0x3F));
    out[3] = (char)(0x80 | (cp & 0x3F));
    return 4;
}

int text_to_utf8(const char* in, int n, int enc_gbk, char* out, int cap)
{
    int o = 0;
    int i = 0;

    if (!out || cap < 1) return 0;
    out[0] = '\0';
    if (!in || n < 0) return 0;

    while (i < n && o + 4 < cap) {
        unsigned char c = (unsigned char)in[i];
        uint32_t cp;
        int adv = 1;

        if (c == 0) break;                     /* 设备常用 0 补齐，到 0 为止 */
        if (c < 0x80) {
            cp = c;
        } else if (enc_gbk) {
            unsigned char t;

            if (i + 1 >= n) { cp = '?'; }
            else {
                t = (unsigned char)in[i + 1];
                if (c < GBK_LEAD_MIN || t < GBK_TRAIL_MIN || t > GBK_TRAIL_MAX) {
                    cp = '?';                  /* 非法前导/后续字节，吞 1 字节 */
                } else {
                    uint16_t u = gbk_uni_map[(c - GBK_LEAD_MIN) * GBK_TRAIL_COUNT +
                                             (t - GBK_TRAIL_MIN)];

                    cp = u ? (uint32_t)u : (uint32_t)'?';
                    adv = 2;
                }
            }
        } else {
            int len = (c >= 0xF0) ? 4 : (c >= 0xE0) ? 3 : (c >= 0xC0) ? 2 : 0;

            if (len == 0 || i + len > n) {
                cp = '?';
            } else {
                uint32_t v = (uint32_t)(c & (0xFF >> (len + 1)));
                int ok = 1;

                for (int k = 1; k < len; k++) {
                    unsigned char cc = (unsigned char)in[i + k];

                    if ((cc & 0xC0) != 0x80) { ok = 0; break; }
                    v = (v << 6) | (uint32_t)(cc & 0x3F);
                }
                cp = ok ? v : (uint32_t)'?';
                if (ok) adv = len;
            }
        }
        o += utf8_put(cp, out + o, cap - o);
        i += adv;
    }
    out[o] = '\0';
    return o;
}

void text_limit_chars(char* s, int max_chars)
{
    int chars = 0;
    int i = 0;

    if (!s) return;
    if (max_chars < 1) { s[0] = '\0'; return; }
    while (s[i]) {
        if (((unsigned char)s[i] & 0xC0) != 0x80) {   /* 只在字符首字节计数 */
            if (chars == max_chars) { s[i] = '\0'; return; }
            chars++;
        }
        i++;
    }
}
