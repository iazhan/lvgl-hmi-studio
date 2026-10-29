/*
 * sha256.c — 精简 SHA-256（FIPS 180-4），只服务口令派生（ADR-0012）
 *
 * 实现取自已公开的标准算法结构：64 轮常量 + 消息扩展。没有动态分配，
 * 口令长度最多几十字节，一次拷进 64 字节块缓冲即可。
 */
#include "sha256.h"

#include <stdio.h>
#include <string.h>

static const uint32_t K[64] = {
    0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u, 0x3956c25bu, 0x59f111f1u,
    0x923f82a4u, 0xab1c5ed5u, 0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u,
    0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u, 0xe49b69c1u, 0xefbe4786u,
    0x0fc19dc6u, 0x240ca1ccu, 0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
    0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u, 0xc6e00bf3u, 0xd5a79147u,
    0x06ca6351u, 0x14292967u, 0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u,
    0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u, 0xa2bfe8a1u, 0xa81a664bu,
    0xc24b8b70u, 0xc76c51a3u, 0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
    0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u, 0x391c0cb3u, 0x4ed8aa4au,
    0x5b9cca4fu, 0x682e6ff3u, 0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u,
    0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u,
};

static uint32_t ror(uint32_t x, int n)
{
    return (x >> n) | (x << (32 - n));
}

static void sha256_block(uint32_t h[8], const uint8_t blk[64])
{
    uint32_t w[64];
    uint32_t a = h[0], b = h[1], c = h[2], d = h[3];
    uint32_t e = h[4], f = h[5], g = h[6], hh = h[7];

    for (int i = 0; i < 16; i++)
        w[i] = ((uint32_t)blk[i * 4] << 24) | ((uint32_t)blk[i * 4 + 1] << 16) |
               ((uint32_t)blk[i * 4 + 2] << 8) | (uint32_t)blk[i * 4 + 3];
    for (int i = 16; i < 64; i++) {
        uint32_t s0 = ror(w[i - 15], 7) ^ ror(w[i - 15], 18) ^ (w[i - 15] >> 3);
        uint32_t s1 = ror(w[i - 2], 17) ^ ror(w[i - 2], 19) ^ (w[i - 2] >> 10);

        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    for (int i = 0; i < 64; i++) {
        uint32_t S1 = ror(e, 6) ^ ror(e, 11) ^ ror(e, 25);
        uint32_t ch = (e & f) ^ (~e & g);
        uint32_t t1 = hh + S1 + ch + K[i] + w[i];
        uint32_t S0 = ror(a, 2) ^ ror(a, 13) ^ ror(a, 22);
        uint32_t mj = (a & b) ^ (a & c) ^ (b & c);
        uint32_t t2 = S0 + mj;

        hh = g;
        g = f;
        f = e;
        e = d + t1;
        d = c;
        c = b;
        b = a;
        a = t1 + t2;
    }
    h[0] += a; h[1] += b; h[2] += c; h[3] += d;
    h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
}

void sha256(const void* data, size_t len, uint8_t out[32])
{
    uint32_t h[8] = { 0x6a09e667u, 0xbb67ae85u, 0x3c6ef372u, 0xa54ff53au,
                      0x510e527fu, 0x9b05688cu, 0x1f83d9abu, 0x5be0cd19u };
    const uint8_t* p = (const uint8_t*)data;
    size_t full = len / 64;
    uint8_t tail[128];
    size_t rem = len % 64;
    size_t tail_len;

    for (size_t i = 0; i < full; i++) sha256_block(h, p + i * 64);

    /* 尾部：剩余数据 + 0x80 + 补零 + 64 位长度（bit） */
    memset(tail, 0, sizeof(tail));
    if (rem) memcpy(tail, p + full * 64, rem);
    tail[rem] = 0x80;
    tail_len = (rem + 1 + 8 <= 64) ? 64 : 128;
    {
        uint64_t bits = (uint64_t)len * 8;

        for (int i = 0; i < 8; i++)
            tail[tail_len - 1 - i] = (uint8_t)((bits >> (8 * i)) & 0xFF);
    }
    sha256_block(h, tail);
    if (tail_len == 128) sha256_block(h, tail + 64);

    for (int i = 0; i < 8; i++) {
        out[i * 4] = (uint8_t)(h[i] >> 24);
        out[i * 4 + 1] = (uint8_t)(h[i] >> 16);
        out[i * 4 + 2] = (uint8_t)(h[i] >> 8);
        out[i * 4 + 3] = (uint8_t)h[i];
    }
}

void sha256_password(const char* salt_hex, const char* password, int rounds,
                     char out_hex[65])
{
    uint8_t d[32];
    char buf[160];
    int n;

    if (rounds < 1) rounds = 1;
    n = snprintf(buf, sizeof(buf), "%s%s", salt_hex ? salt_hex : "",
                 password ? password : "");
    if (n < 0) n = 0;
    if (n > (int)sizeof(buf)) n = (int)sizeof(buf);

    sha256(buf, (size_t)n, d);
    for (int i = 1; i < rounds; i++) {
        uint8_t tmp[32];

        sha256(d, sizeof(d), tmp);
        memcpy(d, tmp, sizeof(d));
    }
    for (int i = 0; i < 32; i++) snprintf(out_hex + i * 2, 3, "%02x", d[i]);
    out_hex[64] = '\0';
}
