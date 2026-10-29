/*
 * sha256.h — 精简 SHA-256（用户口令用，ADR-0012）
 *
 * 板端没有 OpenSSL，这里自带一份：单次摘要 + 口令校验用的多轮迭代。
 * 只用于口令派生，不做流式（不需要，口令最多几十字节）。
 */
#ifndef HMI_SHA256_H
#define HMI_SHA256_H

#include <stddef.h>
#include <stdint.h>

/* 算一段数据的 SHA-256，out 至少 32 字节 */
void sha256(const void* data, size_t len, uint8_t out[32]);

/* 口令摘要：hex(salt) 与 password 拼接后迭代 rounds 轮，输出 64 位小写十六进制 + '\0'。
 * salt_hex 是 16 位十六进制（8 字节），rounds 建议 1000。 */
void sha256_password(const char* salt_hex, const char* password, int rounds,
                     char out_hex[65]);

#endif /* HMI_SHA256_H */
